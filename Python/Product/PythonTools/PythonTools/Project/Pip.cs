// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Interpreter;
using Microsoft.VisualStudio.Shell.Interop;

namespace Microsoft.PythonTools.Project {
    static class Pip {
        private static readonly Regex PackageNameRegex = new Regex(
            "^(?!__pycache__)(?<name>[a-z0-9_]+)(-.+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly KeyValuePair<string, string>[] UnbufferedEnv = new[] { 
            new KeyValuePair<string, string>("PYTHONUNBUFFERED", "1")
        };

        private static readonly Version SupportsDashMPip = new Version(2, 7);

        private static ProcessOutput Run(
            IPythonInterpreterFactory factory,
            Redirector output,
            bool elevate,
            params string[] cmd
        ) {
            factory.ThrowIfNotRunnable("factory");

            IEnumerable<string> args;
            if (factory.Configuration.Version >= SupportsDashMPip) {
                args = new[] { "-m", "pip" }.Concat(cmd);
            } else {
                // Manually quote the code, since we are passing false to
                // quoteArgs below.
                args = new[] { "-c", "\"import pip; pip.main()\"" }.Concat(cmd);
            }

            return ProcessOutput.Run(
                factory.Configuration.InterpreterPath,
                args,
                factory.Configuration.PrefixPath,
                UnbufferedEnv,
                false,
                output,
                quoteArgs: false,
                elevate: elevate
            );
        }

        public static async Task<HashSet<string>> List(IPythonInterpreterFactory factory) {
            using (var proc = Run(factory, null, false, "list")) {
                if (await proc == 0) {
                    return new HashSet<string>(proc.StandardOutputLines
                        .Select(line => Regex.Match(line, "(?<name>.+?) \\((?<version>.+?)\\)"))
                        .Where(match => match.Success)
                        .Select(match => string.Format("{0}=={1}",
                            match.Groups["name"].Value,
                            match.Groups["version"].Value
                        ))
                    );
                }
            }

            // Pip failed, so return a directory listing
            var packagesPath = Path.Combine(factory.Configuration.LibraryPath, "site-packages");
            HashSet<string> result = null;
            if (Directory.Exists(packagesPath)) {
                result = await Task.Run(() => new HashSet<string>(
                    PathUtils.EnumerateDirectories(packagesPath, recurse: false)
                        .Select(path => Path.GetFileName(path))
                        .Select(name => PackageNameRegex.Match(name))
                        .Where(match => match.Success)
                        .Select(match => match.Groups["name"].Value)
                    )
                )
                    .HandleAllExceptions(null, typeof(Pip));
            }

            return result ?? new HashSet<string>();
        }

        public static async Task<HashSet<string>> Freeze(IPythonInterpreterFactory factory) {
            using (var proc = Run(factory, null, false, "freeze")) {
                if (await proc == 0) {
                    return new HashSet<string>(proc.StandardOutputLines);
                }
            }

            // Pip failed, so return an empty set
            return new HashSet<string>();
        }

        /// <summary>
        /// Returns true if installing a package will be secure.
        /// 
        /// This returns false for Python 2.5 and earlier because it does not
        /// include the required SSL support by default. No detection is done to
        /// determine whether the support has been added separately.
        /// </summary>
        public static bool IsSecureInstall(IPythonInterpreterFactory factory) {
            return factory.Configuration.Version > new Version(2, 5);
        }

        private static string GetInsecureArg(
            IPythonInterpreterFactory factory,
            Redirector output = null
        ) {
            if (!IsSecureInstall(factory)) {
                // Python 2.5 does not include ssl, and so the --insecure
                // option is required to use pip.
                if (output != null) {
                    output.WriteErrorLine("Using '--insecure' option for Python 2.5.");
                }
                return "--insecure";
            }
            return null;
        }

        public static async Task<bool> Install(
            IServiceProvider provider,
            IPythonInterpreterFactory factory,
            string package,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            if (!(await factory.FindModulesAsync("pip")).Any()) {
                await InstallPip(provider, factory, elevate, output);
            }
            using (var proc = Run(factory, output, elevate, "install", GetInsecureArg(factory, output), package)) {
                await proc;
                return proc.ExitCode == 0;
            }
        }

        public static async Task<bool> Install(
            IServiceProvider provider,
            IPythonInterpreterFactory factory,
            string package,
            IServiceProvider site,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            if (!(await factory.FindModulesAsync("pip")).Any()) {
                if (site != null) {
                    try {
                        await QueryInstallPip(factory, site, Strings.InstallPip, elevate, output);
                    } catch (OperationCanceledException) {
                        return false;
                    }
                } else {
                    await InstallPip(provider, factory, elevate, output);
                }
            }

            if (output != null) {
                output.WriteLine(Strings.PackageInstalling.FormatUI(package));
                if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForPackageInstallation) {
                    output.ShowAndActivate();
                } else {
                    output.Show();
                }
            }

            using (var proc = Run(factory, output, elevate, "install", GetInsecureArg(factory, output), package)) {
                var exitCode = await proc;

                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(Strings.PackageInstallSucceeded.FormatUI(package));
                    } else {
                        output.WriteLine(Strings.PackageInstallFailedExitCode.FormatUI(package, exitCode));
                    }
                    if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
                return exitCode == 0;
            }
        }

        public static async Task<bool> Uninstall(
            IServiceProvider provider,
            IPythonInterpreterFactory factory,
            string package,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            if (output != null) {
                output.WriteLine(Strings.PackageUninstalling.FormatUI(package));
                if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForPackageInstallation) {
                    output.ShowAndActivate();
                } else {
                    output.Show();
                }
            }

            using (var proc = Run(factory, output, elevate, "uninstall", "-y", package)) {
                var exitCode = await proc;

                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(Strings.PackageUninstallSucceeded.FormatUI(package));
                    } else {
                        output.WriteLine(Strings.PackageUninstallFailedExitCode.FormatUI(package, exitCode));
                    }
                    if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
                return exitCode == 0;
            }
        }

        public static async Task InstallPip(IServiceProvider provider, IPythonInterpreterFactory factory, bool elevate, Redirector output = null) {
            factory.ThrowIfNotRunnable("factory");

            var pipDownloaderPath = PythonToolsInstallPath.GetFile("pip_downloader.py");

            if (output != null) {
                output.WriteLine(Strings.PipInstalling);
                if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForPackageInstallation) {
                    output.ShowAndActivate();
                } else {
                    output.Show();
                }
            }
            using (var proc = ProcessOutput.Run(
                factory.Configuration.InterpreterPath,
                new[] { pipDownloaderPath },
                factory.Configuration.PrefixPath,
                null,
                false,
                output,
                elevate: elevate
            )) {
                var exitCode = await proc;
                if (output != null) {
                    if (exitCode == 0) {
                        output.WriteLine(Strings.PipInstallSucceeded);
                    } else {
                        output.WriteLine(Strings.PipInstallFailedExitCode.FormatUI(exitCode));
                    }
                    if (provider.GetPythonToolsService().GeneralOptions.ShowOutputWindowForPackageInstallation) {
                        output.ShowAndActivate();
                    } else {
                        output.Show();
                    }
                }
            }
        }

        public static async Task<bool> QueryInstall(
            IPythonInterpreterFactory factory,
            string package,
            IServiceProvider site,
            string message,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            if (Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                site,
                message,
                null,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
            ) == 2) {
                throw new OperationCanceledException();
            }

            return await Install(site, factory, package, elevate, output);
        }

        public static async Task QueryInstallPip(
            IPythonInterpreterFactory factory,
            IServiceProvider site,
            string message,
            bool elevate,
            Redirector output = null
        ) {
            factory.ThrowIfNotRunnable("factory");

            if (Microsoft.VisualStudio.Shell.VsShellUtilities.ShowMessageBox(
                site,
                message,
                null,
                OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_OKCANCEL,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
            ) == 2) {
                throw new OperationCanceledException();
            }

            await InstallPip(site, factory, elevate, output);
        }

        /// <summary>
        /// Checks whether a given package is installed and satisfies the
        /// version specification.
        /// </summary>
        /// <param name="package">
        /// Name, and optionally the version of the package to install, in
        /// setuptools format.
        /// </param>
        /// <remarks>
        /// This method requires setuptools to be installed to correctly detect
        /// packages and verify their versions. If setuptools is not available,
        /// the method will always return <c>false</c> for any package name.
        /// </remarks>
        public static async Task<bool> IsInstalled(IPythonInterpreterFactory factory, string package) {
            if (!factory.IsRunnable()) {
                return false;
            }

            var code = string.Format("import pkg_resources; pkg_resources.require('{0}')", package);
            using (var proc = ProcessOutput.Run(
                factory.Configuration.InterpreterPath,
                new[] { "-c", code  },
                factory.Configuration.PrefixPath,
                UnbufferedEnv,
                visible: false,
                redirector: null,
                quoteArgs: true)
            ) {
                return await proc == 0;
            }
        }
    }
}
