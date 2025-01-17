﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    internal class WorkerFunctionInvoker : FunctionInvokerBase
    {
        private readonly Collection<FunctionBinding> _inputBindings;
        private readonly Collection<FunctionBinding> _outputBindings;
        private readonly BindingMetadata _bindingMetadata;
        private readonly ILogger _logger;
        private readonly Action<ScriptInvocationResult> _handleScriptReturnValue;
        private readonly IFunctionInvocationDispatcher _functionDispatcher;
        private readonly IApplicationLifetime _applicationLifetime;
        private readonly TimeSpan _workerInitializationTimeout;

        internal WorkerFunctionInvoker(ScriptHost host, BindingMetadata bindingMetadata, FunctionMetadata functionMetadata, ILoggerFactory loggerFactory,
            Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings, IFunctionInvocationDispatcher functionDispatcher,
            IApplicationLifetime applicationLifetime, TimeSpan workerInitializationTimeout)
            : base(host, functionMetadata, loggerFactory)
        {
            _bindingMetadata = bindingMetadata;
            _inputBindings = inputBindings;
            _outputBindings = outputBindings;
            _functionDispatcher = functionDispatcher;
            _logger = loggerFactory.CreateLogger<WorkerFunctionInvoker>();
            _applicationLifetime = applicationLifetime;
            _workerInitializationTimeout = workerInitializationTimeout;
`
            InitializeFileWatcherIfEnabled();

            if (_outputBindings.Any(p => p.Metadata.IsReturn))
            {
                _handleScriptReturnValue = HandleReturnParameter;
            }
            else
            {
                _handleScriptReturnValue = HandleOutputDictionary;
            }
        }

        protected override async Task<object> InvokeCore(object[] parameters, FunctionInvocationContext context)
        {
            // Need to wait for at least one language worker process to be initialized before accepting invocations
            if (!IsDispatcherReady())
            {
                await DelayUntilFunctionDispatcherInitializedOrShutdown();
            }

            var triggerParameterIndex = 0;
            var cancellationTokenParameterIndex = 4;
            var bindingData = context.Binder.BindingData;
            object triggerValue = TransformInput(parameters[triggerParameterIndex], bindingData);
            var triggerInput = (_bindingMetadata.Name, _bindingMetadata.DataType ?? DataType.String, triggerValue);
            IEnumerable<(string, DataType, object)> inputs = new[] { triggerInput };
            if (_inputBindings.Count > 1)
            {
                var nonTriggerInputs = await BindInputsAsync(context.Binder);
                inputs = inputs.Concat(nonTriggerInputs);
            }

            var invocationContext = new ScriptInvocationContext
            {
                FunctionMetadata = Metadata,
                BindingData = bindingData,
                ExecutionContext = context.ExecutionContext,
                Inputs = inputs,
                ResultSource = new TaskCompletionSource<ScriptInvocationResult>(),
                AsyncExecutionContext = System.Threading.ExecutionContext.Capture(),
                Traceparent = Activity.Current?.Id,
                Tracestate = Activity.Current?.TraceStateString,
                Attributes = Activity.Current?.Tags,
                CancellationToken = HandleCancellationTokenParameter(parameters[cancellationTokenParameterIndex]),
                Logger = context.Logger
            };

            string invocationId = context.ExecutionContext.InvocationId.ToString();
            ScriptInvocationResult result = new();

            _logger.LogTrace("Sending invocation id: '{id}", invocationId);
            await _functionDispatcher.InvokeAsync(invocationContext);

            try
            {
                result = await invocationContext.ResultSource.Task;
            }
            catch (OperationCanceledException ex)
            {
                // Only catch the exception when the task is cancelled, otherwise let it be handled by the ExceptionMiddleware
                throw new FunctionInvocationCanceledException(invocationId, ex);
            }

            await BindOutputsAsync(triggerValue, context.Binder, result);

            return result.Return;
        }

        private bool IsDispatcherReady()
        {
            return _functionDispatcher.State == FunctionInvocationDispatcherState.Initialized || _functionDispatcher.State == FunctionInvocationDispatcherState.Default;
        }

        private async Task DelayUntilFunctionDispatcherInitializedOrShutdown()
        {
            // Don't delay if functionDispatcher is already initialized OR is skipping initialization for one of
            // these reasons: started in placeholder, has no functions, functions do not match set language.

            if (!IsDispatcherReady())
            {
                _logger.LogTrace($"FunctionDispatcher state: {_functionDispatcher.State}");
                bool result = await Utility.DelayAsync((_functionDispatcher.ErrorEventsThreshold + 1) * (int)_workerInitializationTimeout.TotalSeconds, WorkerConstants.WorkerReadyCheckPollingIntervalMilliseconds, () =>
                {
                    return _functionDispatcher.State != FunctionInvocationDispatcherState.Initialized;
                });

                if (result)
                {
                    _logger.LogError($"Final functionDispatcher state: {_functionDispatcher.State}. Initialization timed out and host is shutting down");
                    _applicationLifetime.StopApplication();
                }
            }
        }

        private async Task<(string Name, DataType Type, object Value)[]> BindInputsAsync(Binder binder)
        {
            var bindingTasks = _inputBindings
                .Where(binding => !binding.Metadata.IsTrigger)
                .Select(async (binding) =>
                {
                    BindingContext bindingContext = new BindingContext
                    {
                        Binder = binder,
                        BindingData = binder.BindingData,
                        DataType = binding.Metadata.DataType ?? DataType.String,
                        Cardinality = binding.Metadata.Cardinality ?? Cardinality.One
                    };

                    await binding.BindAsync(bindingContext).ConfigureAwait(false);
                    return (binding.Metadata.Name, bindingContext.DataType, bindingContext.Value);
                });

            return await Task.WhenAll(bindingTasks);
        }

        private async Task BindOutputsAsync(object input, Binder binder, ScriptInvocationResult result)
        {
            if (_outputBindings == null)
            {
                return;
            }

            _handleScriptReturnValue(result);

            var outputBindingTasks = _outputBindings.Select(async binding =>
            {
                // apply the value to the binding
                if (result.Outputs.TryGetValue(binding.Metadata.Name, out object value) && value != null)
                {
                    BindingContext bindingContext = new BindingContext
                    {
                        TriggerValue = input,
                        Binder = binder,
                        BindingData = binder.BindingData,
                        Value = value
                    };
                    await binding.BindAsync(bindingContext).ConfigureAwait(false);
                }
            });

            await Task.WhenAll(outputBindingTasks);
        }

        private object TransformInput(object input, Dictionary<string, object> bindingData)
        {
            if (input is Stream)
            {
                var dataType = _bindingMetadata.DataType ?? DataType.String;
                FunctionBinding.ConvertStreamToValue((Stream)input, dataType, ref input);
            }

            // TODO: investigate moving POCO style binding addition to sdk
            Utility.ApplyBindingData(input, bindingData);
            return input;
        }

        private CancellationToken HandleCancellationTokenParameter(object input)
        {
            if (input == null)
            {
                return CancellationToken.None;
            }

            return (CancellationToken)input;
        }

        private void HandleReturnParameter(ScriptInvocationResult result)
        {
            result.Outputs[ScriptConstants.SystemReturnParameterBindingName] = result.Return;
        }

        private void HandleOutputDictionary(ScriptInvocationResult result)
        {
            if (result.Return is JObject returnJson)
            {
                foreach (var pair in returnJson)
                {
                    result.Outputs[pair.Key] = pair.Value.ToObject<object>();
                }
            }
        }
    }
}