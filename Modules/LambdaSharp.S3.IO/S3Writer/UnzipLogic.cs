﻿/*
 * LambdaSharp (λ#)
 * Copyright (C) 2018-2022
 * lambdasharp.net
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace LambdaSharp.S3.IO.S3Writer;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using LambdaSharp.CustomResource;
using LambdaSharp.Logging;

public class UnzipLogic {

    //--- Class Methods ---
    private static string GetMD5AsBase64(MemoryStream stream) {
        using(var md5 = MD5.Create()) {
            stream.Position = 0;
            var bytes = md5.ComputeHash(stream);
            var result = Convert.ToBase64String(bytes);
            stream.Position = 0;
            return result;
        }
    }

    private static string GetMD5AsHexString(ZipArchiveEntry entry) {
        using(var md5 = MD5.Create())
        using(var hashStream = new CryptoStream(Stream.Null, md5, CryptoStreamMode.Write)) {

            // hash file path
            var filePathBytes = Encoding.UTF8.GetBytes(entry.FullName.Replace('\\', '/'));
            hashStream.Write(filePathBytes, 0, filePathBytes.Length);

            // hash file contents
            using(var stream = entry.Open()) {
                stream.CopyTo(hashStream);
            }
            hashStream.FlushFinalBlock();
            return string.Concat(md5.Hash!.Select(x => x.ToString("X2")));
        }
    }

    //--- Constants ---
    private const int MAX_BATCH_DELETE_OBJECTS = 1000;

    //--- Fields ---
    private readonly ILambdaSharpLogger _logger;
    private readonly string _manifestBucket;
    private readonly IAmazonS3 _s3Client;
    private readonly TransferUtility _transferUtility;

    //--- Constructors ---
    public UnzipLogic(ILambdaSharpLogger logger, string manifestBucket, IAmazonS3 s3Client) {
        _logger = logger;
        _manifestBucket = manifestBucket;
        _s3Client = new AmazonS3Client();
        _transferUtility = new TransferUtility(_s3Client);
    }

    //--- Methods ---
    public async Task<Response<S3WriterResourceAttributes>> Create(S3WriterResourceProperties properties) {
        if(properties.SourceBucketName == null) {
            throw new ArgumentNullException(nameof(properties.SourceBucket));
        }
        if(properties.SourceKey == null) {
            throw new ArgumentNullException(nameof(properties.SourceKey));
        }
        if(properties.DestinationBucketName == null) {
            throw new ArgumentNullException(nameof(properties.DestinationBucket));
        }
        _logger.LogInfo($"copying package s3://{properties.SourceBucketName}/{properties.SourceKey} to S3 bucket {properties.DestinationBucketName}");

        // download package and copy all files to destination bucket
        var fileEntries = new Dictionary<string, string>();
        if(!await ProcessZipFileItemsAsync(properties.SourceBucketName, properties.SourceKey, async entry => {
            await UploadEntry(entry, properties);
            fileEntries.Add(entry.FullName, ComputeEntryHash(entry, properties));
        })) {
            throw new FileNotFoundException("Unable to download source package");
        }

        // create package manifest for future deletion
        _logger.LogInfo($"uploaded {fileEntries.Count:N0} files");
        await WriteManifest(properties, fileEntries);
        return new Response<S3WriterResourceAttributes> {
            PhysicalResourceId = $"s3unzip:{properties.DestinationBucketName}:{properties.DestinationKey}",
            Attributes = new S3WriterResourceAttributes {
                Url = $"s3://{properties.DestinationBucketName}/{properties.DestinationKey}"
            }
        };
    }

    public async Task<Response<S3WriterResourceAttributes>> Update(S3WriterResourceProperties oldProperties, S3WriterResourceProperties properties) {
        if(properties.SourceBucketName == null) {
            throw new ArgumentNullException(nameof(properties.SourceBucket));
        }
        if(properties.SourceKey == null) {
            throw new ArgumentNullException(nameof(properties.SourceKey));
        }
        if(properties.DestinationBucketName == null) {
            throw new ArgumentNullException(nameof(properties.DestinationBucket));
        }
        if(properties.DestinationKey == null) {
            throw new ArgumentNullException(nameof(properties.DestinationKey));
        }

        // check if the unzip properties have changed
        if(
            (oldProperties.DestinationBucketName != properties.DestinationBucketName)
            || (oldProperties.DestinationKey != properties.DestinationKey)
        ) {
            _logger.LogInfo($"replacing package s3://{properties.SourceBucketName}/{properties.SourceKey} in S3 bucket {properties.DestinationBucketName}");

            // remove old file and upload new ones; don't try to compute a diff
            await Delete(oldProperties);
            return await Create(properties);
        } else {
            _logger.LogInfo($"updating package {properties.SourceKey} in S3 bucket {properties.DestinationBucketName}");

            // download old package manifest
            var oldFileEntries = await ReadAndDeleteManifest(oldProperties);
            if(oldFileEntries == null) {

                // unable to download the old manifest; continue with uploading new files
                return await Create(properties);
            }

            // download new source package
            var newFileEntries = new Dictionary<string, string>();
            var uploadedCount = 0;
            var skippedCount = 0;
            if(!await ProcessZipFileItemsAsync(properties.SourceBucketName, properties.SourceKey, async entry => {

                // check if entry has changed using the CRC32 code
                var hash = ComputeEntryHash(entry, properties);
                if(!oldFileEntries.TryGetValue(entry.FullName, out var existingHash) || (existingHash != hash)) {
                    await UploadEntry(entry, properties);
                    ++uploadedCount;
                } else {
                    ++skippedCount;
                }
                newFileEntries.Add(entry.FullName, hash);
            })) {
                throw new FileNotFoundException("Unable to download source package");
            }

            // create package manifest for future deletion
            _logger.LogInfo($"uploaded {uploadedCount:N0} files");
            _logger.LogInfo($"skipped {skippedCount:N0} unchanged files");
            await WriteManifest(properties, newFileEntries);

            // delete files that are no longer needed
            await BatchDeleteFiles(properties.DestinationBucketName, oldFileEntries.Where(kv => !newFileEntries.ContainsKey(kv.Key)).Select(kv => Path.Combine(properties.DestinationKey, kv.Key)).ToList());
            return new Response<S3WriterResourceAttributes> {
                PhysicalResourceId = $"s3unzip:{properties.DestinationBucketName}:{properties.DestinationKey}",
                Attributes = new S3WriterResourceAttributes {
                    Url = $"s3://{properties.DestinationBucketName}/{properties.DestinationKey}"
                }
            };
        }
    }

    public async Task<Response<S3WriterResourceAttributes>> Delete(S3WriterResourceProperties properties) {
        if(properties.SourceKey == null) {
            throw new ArgumentNullException(nameof(properties.SourceKey));
        }
        if(properties.DestinationBucketName == null) {
            throw new ArgumentNullException(nameof(properties.DestinationBucket));
        }
        if(properties.DestinationKey == null) {
            throw new ArgumentNullException(nameof(properties.DestinationKey));
        }
        _logger.LogInfo($"deleting package {properties.SourceKey} from S3 bucket {properties.DestinationBucketName}");

        // download package manifest
        var fileEntries = await ReadAndDeleteManifest(properties);
        if(fileEntries == null) {
            return new Response<S3WriterResourceAttributes>();
        }

        // delete all files from manifest
        await BatchDeleteFiles(
            properties.DestinationBucketName,
            fileEntries.Select(kv => Path.Combine(properties.DestinationKey, kv.Key)).ToList()
        );
        return new Response<S3WriterResourceAttributes>();
    }

    private async Task UploadEntry(ZipArchiveEntry entry, S3WriterResourceProperties properties) {
        if(properties.DestinationKey == null) {
            throw new ArgumentNullException(nameof(properties.DestinationKey));
        }

        // unzip entry
        using(var stream = entry.Open()) {
            var memoryStream = new MemoryStream();

            // determine if stream needs to be encoded
            string? contentEncodingHeader = null;
            var encoding = DetermineEncodingType(entry.FullName, properties);
            switch(encoding) {
            case "NONE":
                await stream.CopyToAsync(memoryStream);
                break;
            case "GZIP":
                contentEncodingHeader = "gzip";
                using(var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true)) {
                    await stream.CopyToAsync(gzipStream);
                }
                break;
            case "BROTLI":
                contentEncodingHeader = "br";
                using(var brotliStream = new BrotliStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true)) {
                    await stream.CopyToAsync(brotliStream);
                }
                break;
            default:
                _logger.LogWarn("unrecognized compression type {0} for {1}", encoding, entry.FullName);
                encoding = "NONE";
                goto case "NONE";
            }
            var base64 = GetMD5AsBase64(memoryStream);

            // only upload file if new or the contents have changed
            var destination = Path.Combine(properties.DestinationKey, entry.FullName).Replace('\\', '/');
            _logger.LogInfo($"uploading file: {destination} [encoding: {encoding.ToLowerInvariant()}]");
            await _transferUtility.UploadAsync(new TransferUtilityUploadRequest {
                Headers = {
                    ContentEncoding = contentEncodingHeader,
                    ContentMD5 = base64
                },
                InputStream = memoryStream,
                BucketName = properties.DestinationBucket,
                Key = destination
            });
        }
    }

    private string DetermineEncodingType(string filename, S3WriterResourceProperties properties)
        => properties.Encoding?.ToUpperInvariant() ?? "NONE";

    private string ComputeEntryHash(ZipArchiveEntry entry, S3WriterResourceProperties properties)
        => $"{GetMD5AsHexString(entry)}-{DetermineEncodingType(entry.FullName, properties)}";

    private async Task<bool> ProcessZipFileItemsAsync(string bucketName, string key, Func<ZipArchiveEntry, Task> callbackAsync) {
        var tmpFilename = Path.GetTempFileName() + ".zip";
        try {
            _logger.LogInfo($"downloading s3://{bucketName}/{key}");
            await _transferUtility.DownloadAsync(new TransferUtilityDownloadRequest {
                BucketName = bucketName,
                Key = key,
                FilePath = tmpFilename
            });
        } catch(Exception e) {
            _logger.LogErrorAsWarning(e, "s3 download failed");
            return false;
        }
        try {
            using(var zip = ZipFile.Open(tmpFilename, ZipArchiveMode.Read)) {
                foreach(var entry in zip.Entries) {
                    await callbackAsync(entry);
                }
            }
        } finally {
            try {
                File.Delete(tmpFilename);
            } catch { }
        }
        return true;
    }

    private async Task WriteManifest(S3WriterResourceProperties properties, Dictionary<string, string> fileEntries) {
        var manifestStream = new MemoryStream();
        using(var manifest = new ZipArchive(manifestStream, ZipArchiveMode.Create, leaveOpen: true))
        using(var manifestEntryStream = manifest.CreateEntry("manifest.txt").Open())
        using(var manifestEntryWriter = new StreamWriter(manifestEntryStream)) {
            await manifestEntryWriter.WriteAsync(string.Join("\n", fileEntries.Select(file => $"{file.Key}\t{file.Value}")));
        }
        await _transferUtility.UploadAsync(
            manifestStream,
            _manifestBucket,
            $"{properties.DestinationBucketName}/{properties.SourceKey}"
        );
    }

    private async Task<Dictionary<string, string>?> ReadAndDeleteManifest(S3WriterResourceProperties properties) {

        // download package manifest
        var fileEntries = new Dictionary<string, string>();
        var key = $"{properties.DestinationBucketName}/{properties.SourceKey}";
        if(!await ProcessZipFileItemsAsync(
            _manifestBucket,
            key,
            async entry => {
                using(var stream = entry.Open())
                using(var reader = new StreamReader(stream)) {
                    var manifest = await reader.ReadToEndAsync();
                    if(!string.IsNullOrWhiteSpace(manifest)) {
                        foreach(var line in manifest.Split('\n')) {
                            var columns = line.Split('\t');
                            fileEntries.Add(columns[0], columns[1]);
                        }
                    }
                }
            }
        )) {
            _logger.LogWarn($"unable to dowload zip file from s3://{_manifestBucket}/{key}");
            return null;
        }

        // delete manifest after reading it
        try {
            await _s3Client.DeleteObjectAsync(new DeleteObjectRequest {
                BucketName = _manifestBucket,
                Key = key
            });
        } catch {
            _logger.LogWarn($"unable to delete manifest file at s3://{_manifestBucket}/{key}");
        }
        return fileEntries;
    }

    private async Task BatchDeleteFiles(string bucketName, IEnumerable<string> keys) {
        if(!keys.Any()) {
            return;
        }
        _logger.LogInfo($"deleting {keys.Count():N0} files");

        // delete all files from manifest
        while(keys.Any()) {
            var batch = keys
                .Take(MAX_BATCH_DELETE_OBJECTS)
                .Select(key => key.Replace('\\', '/'))
                .ToList();
            _logger.LogInfo($"deleting files: {string.Join(", ", batch)}");
            await _s3Client.DeleteObjectsAsync(new DeleteObjectsRequest {
                BucketName = bucketName,
                Objects = batch.Select(filepath => new KeyVersion {
                    Key = filepath
                }).ToList(),
                Quiet = true
            });
            keys = keys.Skip(MAX_BATCH_DELETE_OBJECTS).ToList();
        }
    }
}
