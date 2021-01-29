/*
 * SonarScanner for MSBuild
 * Copyright (C) 2016-2021 SonarSource SA
 * mailto:info AT sonarsource DOT com
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
using System.Linq;
using System.Text;

namespace SonarScanner.MSBuild.PreProcessor
{
    public static class RulesetWriter
    {
        public static string ToString(IEnumerable<string> ids)
        {
            var effectiveIds = ids ?? Enumerable.Empty<string>();

            var duplicates = effectiveIds.GroupBy(id => id).Where(g => g.Count() >= 2).Select(g => g.Key);
            if (duplicates.Any())
            {
                var message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Resources.ERROR_DuplicateCheckId, string.Join(", ", duplicates));
                throw new ArgumentException(message);
            }

            var sb = new StringBuilder();

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<RuleSet Name=\"SonarQube\" Description=\"Rule set generated by SonarQube\" ToolsVersion=\"12.0\">");

            sb.AppendLine("  <Rules AnalyzerId=\"Microsoft.Analyzers.ManagedCodeAnalysis\" RuleNamespace=\"Microsoft.Rules.Managed\">");
            foreach (var id in effectiveIds)
            {
                sb.AppendLine("    <Rule Id=\"" + id + "\" Action=\"Warning\" />");
            }
            sb.AppendLine("  </Rules>");

            sb.AppendLine("</RuleSet>");

            return sb.ToString();
        }
    }
}
