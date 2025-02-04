﻿/*
 * SonarScanner for .NET
 * Copyright (C) 2016-2022 SonarSource SA
 * mailto: info AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SonarScanner.MSBuild.Common;
using SonarScanner.MSBuild.PreProcessor.Roslyn.Model;

namespace SonarScanner.MSBuild.PreProcessor
{
    public sealed class SonarWebService : ISonarQubeServer, IDisposable
    {
        private const string oldDefaultProjectTestPattern = @"[^\\]*test[^\\]*$";
        private readonly string serverUrl;
        private readonly IDownloader downloader;
        private readonly ILogger logger;
        private Version serverVersion;

        public SonarWebService(IDownloader downloader, string server, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(server))
            {
                throw new ArgumentNullException(nameof(server));
            }

            this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            this.serverUrl = server.EndsWith("/", StringComparison.OrdinalIgnoreCase) ? server.Substring(0, server.Length - 1) : server;
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region ISonarQubeServer interface

        public async Task<Tuple<bool, string>> TryGetQualityProfile(string projectKey, string projectBranch, string organization, string language)
        {
            var projectId = GetProjectIdentifier(projectKey, projectBranch);

            var ws = await AddOrganization(GetUrl("/api/qualityprofiles/search?project={0}", projectId), organization);
            this.logger.LogDebug(Resources.MSG_FetchingQualityProfile, projectId, ws);

            var qualityProfileKey = await DoLogExceptions(async () =>
            {
                var result = await this.downloader.TryDownloadIfExists(new Uri(ws));
                string contents = result.Item2;
                if (!result.Item1)
                {
                    ws = await AddOrganization(GetUrl("/api/qualityprofiles/search?defaults=true"), organization);

                    this.logger.LogDebug(Resources.MSG_FetchingQualityProfile, projectId, ws);
                    contents = await DoLogExceptions(async () => await this.downloader.Download(new Uri(ws)) ?? throw new AnalysisException(Resources.ERROR_DownloadingQualityProfileFailed), ws);
                }

                var json = JObject.Parse(contents);
                var profiles = json["profiles"].Children<JObject>();
                JObject profile = null;
                try
                {
                    profile = profiles.SingleOrDefault(p => language.Equals(p["language"].ToString()));
                }
                catch (InvalidOperationException) //As we don't have fail-fast policy for unsupported version for now, we should handle gracefully multi-QPs set for a project, here for SQ < 6.7
                {
                    throw new AnalysisException(Resources.ERROR_UnsupportedSonarQubeVersion);
                }

                if (profile == null)
                {
                    return null;
                }

                return profile["key"].ToString();
            }, ws);

            return new Tuple<bool, string>(qualityProfileKey != null, qualityProfileKey);
        }

        public async Task<IList<SonarRule>> GetRules(string qProfile)
        {
            const int limit = 10000;
            var fetched = 0;
            var total = 1;  // Initial value to enter the loop
            var page = 1;
            var allRules = new List<SonarRule>();
            while (fetched < total && fetched < limit)
            {
                var ws = GetUrl("/api/rules/search?f=repo,name,severity,lang,internalKey,templateKey,params,actives&ps=500&qprofile={0}&p={1}", qProfile, page.ToString());
                this.logger.LogDebug(Resources.MSG_FetchingRules, qProfile, ws);

                allRules.AddRange(await DoLogExceptions(async () =>
                {
                    var contents = await this.downloader.Download(new Uri(ws));
                    var json = JObject.Parse(contents);
                    total = json["total"].ToObject<int>();
                    fetched += json["ps"].ToObject<int>();
                    var rules = json["rules"].Children<JObject>();
                    var actives = json["actives"];

                    return rules.Select(x => CreateRule(x, actives));
                }, ws));

                page++;
            }
            return allRules;
        }

        private async Task<bool> IsSonarCloud() =>
            SonarProduct.IsSonarCloud(this.serverUrl, await GetServerVersion());

        public async Task WarnIfSonarQubeVersionIsDeprecated()
        {
            var version = await GetServerVersion();
            if (!await IsSonarCloud() && version.CompareTo(new Version(7, 9)) < 0)
            {
                this.logger.LogWarning(Resources.WARN_SonarQubeDeprecated);
            }
        }

        public async Task<bool> IsServerLicenseValid()
        {
            if (await IsSonarCloud())
            {
                this.logger.LogDebug(Resources.MSG_SonarCloudDetected_SkipLicenseCheck);
                return true;
            }
            else
            {
                this.logger.LogDebug(Resources.MSG_CheckingLicenseValidity);
                var ws = GetUrl("/api/editions/is_valid_license");
                var response = await this.downloader.TryGetLicenseInformation(new Uri(ws));

                var content = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    var json = JObject.Parse(content);

                    var jsonErrors = json["errors"];

                    if (jsonErrors?.Any(x => x["msg"]?.Value<string>() == "License not found") == true)
                    {
                        return false;
                    }

                    this.logger.LogDebug(Resources.MSG_CE_Detected_LicenseValid);
                    return true;
                }
                else
                {
                    var json = JObject.Parse(content);
                    return json["isValidLicense"].ToObject<bool>();
                }
            }
        }

        private static SonarRule CreateRule(JObject r, JToken actives)
        {
            var active = actives?.Value<JArray>(r["key"].ToString())?.FirstOrDefault();
            var rule = new SonarRule(r["repo"].ToString(), ParseRuleKey(r["key"].ToString()), r["internalKey"]?.ToString(), r["templateKey"]?.ToString(), active != null);
            if (active != null)
            {
                rule.Parameters = active["params"].Children<JObject>().ToDictionary(pair => pair["key"].ToString(), pair => pair["value"].ToString());
                if (rule.Parameters.ContainsKey("CheckId"))
                {
                    rule.RuleKey = rule.Parameters["CheckId"];
                }
            }
            return rule;
        }

        /// <summary>
        /// Retrieves project properties from the server.
        ///
        /// Will fail with an exception if the downloaded return from the server is not a JSON array.
        /// </summary>
        /// <param name="projectKey">The SonarQube project key to retrieve properties for.</param>
        /// <param name="projectBranch">The SonarQube project branch to retrieve properties for (optional).</param>
        /// <returns>A dictionary of key-value property pairs.</returns>
        ///
        public async Task<IDictionary<string, string>> GetProperties(string projectKey, string projectBranch)
        {
            if (string.IsNullOrWhiteSpace(projectKey))
            {
                throw new ArgumentNullException(nameof(projectKey));
            }

            var projectId = GetProjectIdentifier(projectKey, projectBranch);

            if (await IsSonarCloud() || (await GetServerVersion()).CompareTo(new Version(6, 3)) >= 0)
            {
                return await GetComponentProperties(projectId);
            }
            else
            {
                return await GetComponentPropertiesLegacy(projectId);
            }
        }

        public async Task<Version> GetServerVersion()
        {
            if (this.serverVersion == null)
            {
                await DownloadServerVersion();
            }
            return this.serverVersion;
        }

        public async Task<IEnumerable<string>> GetAllLanguages()
        {
            var ws = GetUrl("/api/languages/list");
            return await DoLogExceptions(async () =>
            {
                var contents = await this.downloader.Download(new Uri(ws));

                var langArray = JObject.Parse(contents).Value<JArray>("languages");
                return langArray.Select(obj => obj["key"].ToString());
            }, ws);
        }

        public async Task<bool> TryDownloadEmbeddedFile(string pluginKey, string embeddedFileName, string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(pluginKey))
            {
                throw new ArgumentNullException(nameof(pluginKey));
            }
            if (string.IsNullOrWhiteSpace(embeddedFileName))
            {
                throw new ArgumentNullException(nameof(embeddedFileName));
            }
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new ArgumentNullException(nameof(targetDirectory));
            }

            var url = GetUrl("/static/{0}/{1}", pluginKey, embeddedFileName);

            return await DoLogExceptions(async () =>
            {
                var targetFilePath = Path.Combine(targetDirectory, embeddedFileName);

                this.logger.LogDebug(Resources.MSG_DownloadingZip, embeddedFileName, url, targetDirectory);
                return await this.downloader.TryDownloadFileIfExists(new Uri(url), targetFilePath);
            }, url);
        }

        #endregion ISonarQubeServer interface

        #region Private methods

        private async Task<string> AddOrganization(string encodedUrl, string organization)
        {
            if (string.IsNullOrEmpty(organization))
            {
                return encodedUrl;
            }
            var version = await GetServerVersion();
            if (version.CompareTo(new Version(6, 3)) >= 0)
            {
                return EscapeQuery(encodedUrl + "&organization={0}", organization);
            }

            return encodedUrl;
        }

        private async Task<T> DoLogExceptions<T>(Func<Task<T>> op, string url)
        {
            try
            {
                return await op();
            }
            catch (Exception e)
            {
                this.logger.LogError("Failed to request and parse '{0}': {1}", url, e.Message);
                throw;
            }
        }

        private async Task DownloadServerVersion()
        {
            var ws = GetUrl("api/server/version");
            this.serverVersion = await DoLogExceptions(async () =>
            {
                var contents = await this.downloader.Download(new Uri(ws));
                var separator = contents.IndexOf('-');
                return separator >= 0 ? new Version(contents.Substring(0, separator)) : new Version(contents);
            }, ws);
        }

        private async Task<IDictionary<string, string>> GetComponentPropertiesLegacy(string projectId)
        {
            var ws = GetUrl("/api/properties?resource={0}", projectId);
            this.logger.LogDebug(Resources.MSG_FetchingProjectProperties, projectId, ws);
            var result = await DoLogExceptions(async () =>
            {
                var contents = await this.downloader.Download(new Uri(ws), true);
                var properties = JArray.Parse(contents);
                return properties.ToDictionary(p => p["key"].ToString(), p => p["value"].ToString());
            }, ws);

            return CheckTestProjectPattern(result);
        }

        private async Task<IDictionary<string, string>> GetComponentProperties(string projectId)
        {
            var ws = GetUrl("/api/settings/values?component={0}", projectId);
            this.logger.LogDebug(Resources.MSG_FetchingProjectProperties, projectId, ws);

            var projectFound = await DoLogExceptions(async () => await this.downloader.TryDownloadIfExists(new Uri(ws), true), ws);

            var contents = projectFound?.Item2;

            if (projectFound != null && !projectFound.Item1)
            {
                ws = GetUrl("/api/settings/values");
                this.logger.LogDebug("No settings for project {0}. Getting global settings: {1}", projectId, ws);
                contents = await DoLogExceptions(async () => await this.downloader.Download(new Uri(ws)), ws);
            }

            return await DoLogExceptions(async () => ParseSettingsResponse(contents), ws);
        }

        private Dictionary<string, string> ParseSettingsResponse(string contents)
        {
            var settings = new Dictionary<string, string>();
            var settingsArray = JObject.Parse(contents).Value<JArray>("settings");
            foreach (var t in settingsArray)
            {
                GetPropertyValue(settings, t);
            }

            return CheckTestProjectPattern(settings);
        }

        private Dictionary<string, string> CheckTestProjectPattern(Dictionary<string, string> settings)
        {
            // http://jira.sonarsource.com/browse/SONAR-5891 and https://jira.sonarsource.com/browse/SONARMSBRU-285
            if (settings.ContainsKey("sonar.cs.msbuild.testProjectPattern"))
            {
                var value = settings["sonar.cs.msbuild.testProjectPattern"];
                if (value != oldDefaultProjectTestPattern)
                {
                    this.logger.LogWarning("The property 'sonar.cs.msbuild.testProjectPattern' defined in SonarQube is deprecated. Set the property 'sonar.msbuild.testProjectPattern' in the scanner instead.");
                }
                settings["sonar.msbuild.testProjectPattern"] = value;
                settings.Remove("sonar.cs.msbuild.testProjectPattern");
            }
            return settings;
        }

        private void GetPropertyValue(Dictionary<string, string> settings, JToken p)
        {
            var key = p["key"].ToString();
            if (p["value"] != null)
            {
                var value = p["value"].ToString();
                settings.Add(key, value);
            }
            else if (p["fieldValues"] != null)
            {
                MultivalueToProps(settings, key, (JArray)p["fieldValues"]);
            }
            else if (p["values"] != null)
            {
                var array = (JArray)p["values"];
                var value = string.Join(",", array.Values<string>());
                settings.Add(key, value);
            }
            else
            {
                throw new ArgumentException("Invalid property");
            }
        }

        private void MultivalueToProps(Dictionary<string, string> props, string settingKey, JArray array)
        {
            var id = 1;
            foreach (var obj in array.Children<JObject>())
            {
                foreach (var prop in obj.Properties())
                {
                    var key = string.Concat(settingKey, ".", id, ".", prop.Name);
                    var value = prop.Value.ToString();
                    props.Add(key, value);
                }
                id++;
            }
        }

        private static string ParseRuleKey(string key)
        {
            var pos = key.IndexOf(':');
            return key.Substring(pos + 1);
        }

        /// <summary>
        /// Concatenates project key and branch into one string.
        /// </summary>
        /// <param name="projectKey">Unique project key</param>
        /// <param name="projectBranch">Specified branch of the project. Null if no branch to be specified.</param>
        /// <returns>A correctly formatted branch-specific identifier (if appropriate) for a given project.</returns>
        private static string GetProjectIdentifier(string projectKey, string projectBranch = null)
        {
            var projectId = projectKey;
            if (!string.IsNullOrWhiteSpace(projectBranch))
            {
                projectId = projectKey + ":" + projectBranch;
            }

            return projectId;
        }

        private string GetUrl(string format, params string[] args)
        {
            var queryString = EscapeQuery(format, args);
            if (!queryString.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                queryString = string.Concat('/', queryString);
            }

            return this.serverUrl + queryString;
        }

        private string EscapeQuery(string format, params string[] args)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args.Select(a => WebUtility.UrlEncode(a)).ToArray());
        }

        #endregion Private methods

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        private void Dispose(bool disposing)
        {
            if (!this.disposedValue)
            {
                if (disposing)
                {
                    Utilities.SafeDispose(this.downloader);
                }

                this.disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }

        #endregion IDisposable Support
    }
}
