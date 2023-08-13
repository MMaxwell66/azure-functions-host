// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace Microsoft.Azure.WebJobs.Script.Workers.Rpc
{
    // 描述了启动worker的一些东西
    public class RpcWorkerConfig
    {
        public RpcWorkerDescription Description { get; set; }

        public WorkerProcessArguments Arguments { get; set; }

        public WorkerProcessCountOptions CountOptions { get; set; }
    }
}
