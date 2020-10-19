﻿// <copyright file="XRayPipelineHandler.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Contrib.Extensions.AWSXRay.Trace;
using OpenTelemetry.Trace;

namespace Opentelemetry.Contrib.Extensions.AWSXRay.Instrumentation
{
    internal class XRayPipelineHandler : PipelineHandler
    {
        internal const string ActivitySourceName = "Amazon.AWS.AWSClientInstrumentation";
        private const string AWSRequestIdSemanticConvention = "aws.requestId";

        private static readonly AWSXRayPropagator AwsPropagator = new AWSXRayPropagator();
        private static readonly Action<IDictionary<string, string>, string, string> Setter = (carrier, name, value) =>
        {
            carrier[name] = value;
        };

        private static readonly Dictionary<string, string> ServiceParameterMap = new Dictionary<string, string>()
        {
            { "DynamoDBv2", "TableName" },
            { "SQS", "QueueUrl" },
        };

        private static readonly Dictionary<string, string> ParameterAttributeMap = new Dictionary<string, string>()
        {
            { "TableName", "aws.table_name" },
            { "QueueUrl", "aws.queue_url" },
        };

        private static readonly ActivitySource AWSSDKActivitySource = new ActivitySource(ActivitySourceName);

        public override void InvokeSync(IExecutionContext executionContext)
        {
            var activity = this.ProcessBeginRequest(executionContext);
            try
            {
                base.InvokeSync(executionContext);
            }
            catch (Exception ex)
            {
                if (activity != null)
                {
                    this.ProcessException(activity, ex);
                }
            }
            finally
            {
                if (activity != null)
                {
                    this.ProcessEndRequest(executionContext, activity);
                }
            }
        }

        public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            T ret = null;

            var activity = this.ProcessBeginRequest(executionContext);
            try
            {
                ret = await base.InvokeAsync<T>(executionContext).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (activity != null)
                {
                    this.ProcessException(activity, ex);
                }
            }
            finally
            {
                if (activity != null)
                {
                    this.ProcessEndRequest(executionContext, activity);
                }
            }

            return ret;
        }

        private Activity ProcessBeginRequest(IExecutionContext executionContext)
        {
            Activity activity = null;

            var requestContext = executionContext.RequestContext;
            var service = this.GetAWSServiceName(requestContext);
            var operation = this.GetAWSOperationName(requestContext);

            activity = AWSSDKActivitySource.StartActivity(service + "." + operation, ActivityKind.Client);

            if (activity == null)
            {
                return null;
            }

            activity.AddTag("aws.service", service);
            activity.AddTag("aws.operation", operation);

            var client = executionContext.RequestContext.ClientConfig;
            if (client != null)
            {
                activity.AddTag("aws.region", client.RegionEndpoint?.SystemName);
            }

            this.AddRequestSpecificInformation(activity, requestContext, service);

            AwsPropagator.Inject(new PropagationContext(activity.Context, Baggage.Current), requestContext.Request.Headers, Setter);

            return activity;
        }

        private void ProcessEndRequest(IExecutionContext executionContext, Activity activity)
        {
            var responseContext = executionContext.ResponseContext;
            var requestContext = executionContext.RequestContext;

            if (activity.GetTagValue(AWSRequestIdSemanticConvention) == null)
            {
                activity.AddTag(AWSRequestIdSemanticConvention, this.FetchRequestId(requestContext, responseContext));
            }

            var httpResponse = responseContext.HttpResponse;
            if (httpResponse != null)
            {
                this.AddStatusCodeToActivity(activity, (int)httpResponse.StatusCode);
                activity.SetTag("http.response_content_length", httpResponse.ContentLength);
            }

            activity.Stop();
        }

        private void ProcessException(Activity activity, Exception ex)
        {
            activity.RecordException(ex);

            activity.SetStatus(Status.Error.WithDescription(ex.Message));

            if (ex is AmazonServiceException amazonServiceException)
            {
                this.AddStatusCodeToActivity(activity, (int)amazonServiceException.StatusCode);
                activity.AddTag(AWSRequestIdSemanticConvention, amazonServiceException.RequestId);
            }
        }

        private string GetAWSServiceName(IRequestContext requestContext)
        {
            string serviceName = string.Empty;
            serviceName = Utils.RemoveAmazonPrefixFromServiceName(requestContext.Request.ServiceName);
            return serviceName;
        }

        private string GetAWSOperationName(IRequestContext requestContext)
        {
            string operationName = string.Empty;
            string completeRequestName = requestContext.OriginalRequest.GetType().Name;
            string suffix = "Request";
            operationName = Utils.RemoveSuffix(completeRequestName, suffix);
            return operationName;
        }

        private void AddRequestSpecificInformation(Activity activity, IRequestContext requestContext, string service)
        {
            AmazonWebServiceRequest request = requestContext.OriginalRequest;

            if (ServiceParameterMap.TryGetValue(service, out string parameter))
            {
                var property = request.GetType().GetProperty(parameter);
                if (property != null)
                {
                    if (ParameterAttributeMap.TryGetValue(parameter, out string attribute))
                    {
                        activity.AddTag(attribute, property.GetValue(request));
                    }
                }
            }
        }

        private void AddStatusCodeToActivity(Activity activity, int status_code)
        {
            // TODO: Convert to use semantic conventions but the SemanticConventions class is internal
            activity.SetTag("http.status_code", status_code);
        }

        private string FetchRequestId(IRequestContext requestContext, IResponseContext responseContext)
        {
            string request_id = string.Empty;
            var response = responseContext.Response;
            if (response != null)
            {
                request_id = response.ResponseMetadata.RequestId;
            }
            else
            {
                var request_headers = requestContext.Request.Headers;
                if (string.IsNullOrEmpty(request_id) && request_headers.TryGetValue("x-amzn-RequestId", out string req_id))
                {
                    request_id = req_id;
                }

                if (string.IsNullOrEmpty(request_id) && request_headers.TryGetValue("x-amz-request-id", out req_id))
                {
                    request_id = req_id;
                }

                if (string.IsNullOrEmpty(request_id) && request_headers.TryGetValue("x-amz-id-2", out req_id))
                {
                    request_id = req_id;
                }
            }

            return request_id;
        }
    }
}
