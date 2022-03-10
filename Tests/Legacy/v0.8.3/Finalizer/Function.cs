using System;
using System.Threading;
using System.Threading.Tasks;
using LambdaSharp;
using LambdaSharp.Finalizer;

namespace Legacy.ModuleV082.Finalizer {

    public sealed class Function : ALambdaFinalizerFunction {

        //--- Methods ---
        public override async Task InitializeAsync(LambdaConfig config) {

            // TO-DO: add function initialization and reading configuration settings
        }

        public override async Task CreateDeploymentAsync(FinalizerProperties current, CancellationToken cancellationToken) {

            // TO-DO: add business logic when creating a CloudFormation stack
        }

        public override async Task UpdateDeploymentAsync(FinalizerProperties current, FinalizerProperties previous, CancellationToken cancellationToken) {

            // TO-DO: add business logic when updating a CloudFormation stack
        }

        public override async Task DeleteDeploymentAsync(FinalizerProperties current, CancellationToken cancellationToken) {

            // TO-DO: add business logic when deleting a CloudFormation stack
        }
    }
}
