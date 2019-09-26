﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using GcmSharp.Serialization;
using RestSharp;
using UniFiSharp.Json;

namespace UniFiSharp
{
    internal class DefaultUniFiRestClient : RestClient, IUniFiRestClient
    {
        private readonly string _username;
        private readonly string _password;

        internal DefaultUniFiRestClient(Uri baseUrl, string username, string password, bool ignoreSslValidation) : base(baseUrl)
        {
            _username = username;
            _password = password;

            CookieContainer = new CookieContainer();

            AddHandler("application/json", NewtonsoftJsonSerializer.Default);
            AddHandler("text/json", NewtonsoftJsonSerializer.Default);
            AddHandler("text/x-json", NewtonsoftJsonSerializer.Default);
            AddHandler("text/javascript", NewtonsoftJsonSerializer.Default);
            AddHandler("*+json", NewtonsoftJsonSerializer.Default);

            if (ignoreSslValidation)
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
            }
        }

        public async Task UniFiGet(string url)
        {
            await UniFiRequest(Method.GET, url);
        }

        public async Task<T> UniFiGet<T>(string url) where T : new()
        {
            return await UniFiRequest<T>(Method.GET, url);
        }

        public async Task<IList<T>> UniFiGetMany<T>(string url) where T : new()
        {
            return await UniFiRequestMany<T>(Method.GET, url);
        }

        public async Task UniFiPost(string url, object jsonBody)
        {
            await UniFiRequest(Method.POST, url, jsonBody);
        }

        public async Task<T> UniFiPost<T>(string url, object jsonBody) where T : new()
        {
            return await UniFiRequest<T>(Method.POST, url, jsonBody);
        }

        public async Task<IList<T>> UniFiPostMany<T>(string url, object jsonBody) where T : new()
        {
            return await UniFiRequestMany<T>(Method.POST, url, jsonBody);
        }

        public async Task UniFiPut(string url, object jsonBody)
        {
            await UniFiRequest(Method.PUT, url, jsonBody);
        }

        public async Task<T> UniFiPut<T>(string url, object jsonBody) where T : new()
        {
            return await UniFiRequest<T>(Method.PUT, url, jsonBody);
        }

        public async Task<IList<T>> UniFiPutMany<T>(string url, object jsonBody) where T : new()
        {
            return await UniFiRequestMany<T>(Method.PUT, url, jsonBody);
        }

        public async Task UniFiDelete(string url)
        {
            await UniFiRequest(Method.DELETE, url);
        }

        public async Task UnifiFileUpload(string url, string name, string fileName, string contentType, byte[] data)
        {
            await UnifiMultipartFormRequest(url, name, fileName, contentType, data);
        }

        private async Task UniFiRequest(Method method, string url, object jsonBody = null)
        {
            var request = new RestRequest(url, method);
            if ((method == Method.POST || method == Method.PUT) && jsonBody != null)
                request.AddJsonBody(jsonBody);
            await ExecuteRequest<object>(request);
        }

        private async Task<T> UniFiRequest<T>(Method method, string url, object jsonBody = null) where T : new()
        {
            var request = new RestRequest(url, method);
            if ((method == Method.POST || method == Method.PUT) && jsonBody != null)
                request.AddJsonBody(jsonBody);

            var envelope = await ExecuteRequest<T>(request);
            if (envelope.Data != null && envelope.Data.Length > 0)
            {
                return envelope.Data[0];
            }

            if (envelope.Metadata.ResultCode.ToLower() == "error")
            {
                throw new UniFiApiException($"UniFi API returned an error: {envelope.Metadata.Message}");
            }

            return default;
        }

        private async Task<IList<T>> UniFiRequestMany<T>(Method method, string url, object jsonBody = null) where T : new()
        {
            var request = new RestRequest(url, method);
            if ((method == Method.POST || method == Method.PUT) && jsonBody != null)
                request.AddJsonBody(jsonBody);
            var envelope = await ExecuteRequest<T>(request);
            return envelope.Data == null ? new List<T>() : new List<T>(envelope.Data);
        }

        public async Task Authenticate()
        {
            await UniFiPost("api/login", new
            {
                username = _username,
                password = _password,
                remember = false,
                strict = true
            });
        }

        private async Task<JsonMessageEnvelope<T>> ExecuteRequest<T>(IRestRequest request, bool attemptReauthentication = true) where T : new()
        {
            this.AddDefaultHeader("Referrer", BaseUrl.ToString());
            FollowRedirects = true;

            if (CookieContainer.GetCookies(BaseUrl).Count > 0)
            {
                try
                {
                    this.AddDefaultHeader("X-Csrf-Token", CookieContainer.GetCookies(BaseUrl)["csrf_token"].Value);
                }
                catch
                {
                }
            }

            request.RequestFormat = DataFormat.Json;
            request.JsonSerializer = NewtonsoftJsonSerializer.Default;

            var response = await ExecuteTaskAsync<JsonMessageEnvelope<T>>(request);
            var envelope = response.Data;

            if (envelope == null && !response.IsSuccessful)
                throw response.ErrorException;

            if (!envelope.IsSuccessfulResponse &&
                envelope.Metadata.Message == "api.err.LoginRequired" &&
                attemptReauthentication)
            {
                await Authenticate();
                return await ExecuteRequest<T>(request, false);
            }

            return response.Data;
        }

        /// <summary>
        ///     Upload a file to the UniFi controller. The only known use of this at the moment is for uploading .ogg files for the
        ///     AP-AC-EDU APs
        /// </summary>
        /// <param name="url"></param>
        /// <param name="name"></param>
        /// <param name="fileName"></param>
        /// <param name="contentType"></param>
        /// <param name="data"></param>
        /// <param name="attemptReauthentication"></param>
        /// <returns></returns>
        private async Task UnifiMultipartFormRequest(string url, string name, string fileName, string contentType, byte[] data, bool attemptReauthentication = true)
        {
            // Note the UniFi controller will return 404 when uploading a file - however the file *is* successfully uploaded. 

            this.AddDefaultHeader("Referrer", BaseUrl.ToString());

            // Bodge to work around the fact uploads don't return the normal metadata if unauthorized
            FollowRedirects = false;

            if (CookieContainer.GetCookies(BaseUrl).Count > 0)
            {
                try
                {
                    this.AddDefaultHeader("X-Csrf-Token", CookieContainer.GetCookies(BaseUrl)["csrf_token"].Value);
                }
                catch
                {
                }
            }

            var request = new RestRequest(url, Method.POST)
            {
                AlwaysMultipartFormData = true
            };

            request.AddParameter("name", name, ParameterType.RequestBody);
            request.AddFileBytes("filedata", data, fileName, contentType);

            var response = await ExecuteTaskAsync(request);

            // Bodge to authenticate if needed (if we're being redirected back to the login page, then we need to attempt to authenticate)
            if (response.StatusCode == HttpStatusCode.Redirect)
            {
                var redirectLocation = response.Headers.ToList().Find(x => x.Name == "Location").Value.ToString();

                if (redirectLocation.Contains("/manage/account/login?redirect"))
                {
                    await Authenticate();
                    await UnifiMultipartFormRequest(url, name, fileName, contentType, data, false);
                }
            }
        }
    }
}