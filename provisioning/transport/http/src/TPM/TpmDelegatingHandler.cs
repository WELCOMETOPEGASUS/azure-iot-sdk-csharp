﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Devices.Provisioning.Client.Transport.Http
{
    internal class TpmDelegatingHandler : DelegatingHandler
    {
        internal const string ProvisioningHeaderName = "drs-set-sas-token";
        private const string KeyName = "registration";
        private readonly TimeSpan _timeToLive = TimeSpan.FromDays(1);
        private readonly ProvisioningSecurityClientSasToken _securityClient;

        public TpmDelegatingHandler(ProvisioningSecurityClientSasToken securityClient)
        {
            _securityClient = securityClient;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, 
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if(response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (request.Properties.TryGetValue(ProvisioningHeaderName, out object result))
                {
                    if (result is Action<string> setSasToken)
                    {
                        string target = GetTarget(request.RequestUri.LocalPath);
                        string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        TpmChallenge challenge = JsonConvert.DeserializeObject<TpmChallenge>(responseContent);

                        string sasToken = 
                            await ExtractServiceAuthKey(target, challenge.AuthenticationKey).ConfigureAwait(false);

                        setSasToken(sasToken);
                        response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            return response;
        }

        private string GetTarget(string requestUriLocalPath)
        {
            requestUriLocalPath = requestUriLocalPath.TrimStart('/');
            string[] parameters = requestUriLocalPath.Split('/');
            if (parameters.Length <= 3)
            {
                throw new ArgumentException($"Invalid RequestUri LocalPath");
            }

            return string.Concat(parameters[0], "/" , parameters[1], "/", parameters[2]);
        }

        public async Task<string> ExtractServiceAuthKey(string hostName, string authenticationKey)
        {
            await _securityClient.ActivateSymmetricIdentityAsync(
                Convert.FromBase64String(authenticationKey)).ConfigureAwait(false);

            string sasToken = await BuildSasSignature(
                KeyName,
                hostName,
                _timeToLive).ConfigureAwait(false);

            return sasToken;
        }

        private async Task<string> BuildSasSignature(string keyName, string target, TimeSpan timeToLive)
        {
            string expiresOn = BuildExpiresOn(timeToLive);
            string audience = WebUtility.UrlEncode(target);
            var fields = new List<string>
            {
                audience,
                expiresOn
            };

            // Example string to be signed:
            // dh://myiothub.azure-devices.net/a/b/c?myvalue1=a
            // <Value for ExpiresOn>

            byte[] signedBytes = 
                await _securityClient.SignAsync(Encoding.UTF8.GetBytes(string.Join("\n", fields))).ConfigureAwait(false);

            string signature = Convert.ToBase64String(signedBytes);

            // Example returned string:
            // SharedAccessSignature sr=ENCODED(dh://myiothub.azure-devices.net/a/b/c?myvalue1=a)&sig=<Signature>&se=<ExpiresOnValue>[&skn=<KeyName>]

            var buffer = new StringBuilder();
            buffer.AppendFormat(CultureInfo.InvariantCulture, "{0} {1}={2}&{3}={4}&{5}={6}",
                "SharedAccessSignature",
                "sr", audience,
                "sig", WebUtility.UrlEncode(signature),
                "se", WebUtility.UrlEncode(expiresOn));

            if (!string.IsNullOrEmpty(keyName))
            {
                buffer.AppendFormat(CultureInfo.InvariantCulture, "&{0}={1}",
                    "skn", WebUtility.UrlEncode(keyName));
            }

            return buffer.ToString();
        }

        private static string BuildExpiresOn(TimeSpan timeToLive)
        {
            DateTime epochTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            DateTime expiresOn = DateTime.UtcNow.Add(timeToLive);
            TimeSpan secondsFromBaseTime = expiresOn.Subtract(epochTime);
            long seconds = Convert.ToInt64(secondsFromBaseTime.TotalSeconds, CultureInfo.InvariantCulture);
            return Convert.ToString(seconds, CultureInfo.InvariantCulture);
        }
    }
}
