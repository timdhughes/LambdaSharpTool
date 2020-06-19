$Env:LAMBDASHARP_VERSION_PREFIX="0.8.0.3"
$Env:LAMBDASHARP_VERSION_SUFFIX=""

# create full version text
if($Env:LAMBDASHARP_VERSION_SUFFIX -eq $null) {
    $Env:LAMBDASHARP_VERSION="$Env:LAMBDASHARP_VERSION_PREFIX"
} else {
    $Env:LAMBDASHARP_VERSION="$Env:LAMBDASHARP_VERSION_PREFIX-$Env:LAMBDASHARP_VERSION_SUFFIX"
}
