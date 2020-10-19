﻿// <copyright file="MockHttpRequestFactory.cs" company="OpenTelemetry Authors">
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
using System.Net.Http;
using Amazon.Runtime;

namespace OpenTelemetry.Contrib.Extensions.AWSXRay.Tests
{
    public class MockHttpRequestFactory : IHttpRequestFactory<HttpContent>
    {
        public Action GetResponseAction { get; set; }

        public MockHttpRequest LastCreatedRequest { get; private set; }

        public Func<MockHttpRequest, HttpResponseMessage> ResponseCreator { get; set; }

        public IHttpRequest<HttpContent> CreateHttpRequest(Uri requestUri)
        {
            this.LastCreatedRequest = new MockHttpRequest(requestUri, this.GetResponseAction, this.ResponseCreator);
            return this.LastCreatedRequest;
        }

        public void Dispose()
        {
        }
    }
}
