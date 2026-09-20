// Copyright (c) Microsoft Corporation. Licensed under the MIT License.

using CommandLine;
using CommandLine.Text;
using DiffPlex.DiffBuilder.Model;
using Microsoft.CST.OpenSource.Reproducibility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static Crayon.Output;

namespace Microsoft.CST.OpenSource
{
    using Microsoft.CST.OpenSource.Helpers;
    using OssGadget.Options;
    using PackageManagers;
    using PackageUrl;

    public enum DiffTechnique
    {
        Strict,
        Normalized
    };

    public class ReproducibleTool : BaseTool<ReproducibleToolOptions>
    {
        public ReproducibleTool(ProjectManagerFactory projectManagerFactory) : base(projectManagerFactory)
        {
        }

        public ReproducibleTool() : this(new ProjectManagerFactory())
        {
        }

        /// <summary>
        /// Algorithm: 0.0 = Worst, 1.0 = Best 1.0 =&gt; Bit for bit archive match. @TODO Refactor
        /// this into the individual strategy objects.
        /// </summary>
        /// <param name="result"></param>
        /// <returns></returns>
        public KeyValuePair<double, string> GetReproducibilityScore(ReproducibleToolResult fullResult)
        {
            if (fullResult.Results == null)
            {
                return KeyValuePair.Create(0.0, "No semantic equivalency results were created.");
            }

            KeyValuePair<double, string> bestScore = KeyValuePair.Create(0.0, "No strategies were able to successfully derive the package from the source code.");

            foreach (StrategyResult? result in fullResult.Results)
            {
                string? filesString = result.NumIgnoredFiles == 1 ? "file" : "files";

                if (string.Equals(result.StrategyName, "PackageMatchesSourceStrategy"))
                {
                    if (result.IsSuccess && !result.IsError)
                    {
                        if (result.NumIgnoredFiles == 0)
                        {
                            if (bestScore.Key < 0.80)
                            {
                                bestScore = KeyValuePair.Create(0.80, $"Package contents match the source repository contents, file-by-file, with no ignored {filesString}.");
                            }
                        }
                        else
                        {
                            if (bestScore.Key < 0.70)
                            {
                                bestScore = KeyValuePair.Create(0.70, $"Package contents match the source repository contents, file-by-file, with {result.NumIgnoredFiles} ignored {filesString}.");
                            }
                        }
                    }
                }
                else if (string.Equals(result.StrategyName, "PackageContainedInSourceStrategy"))
                {
                    if (result.IsSuccess && !result.IsError)
                    {
                        if (result.NumIgnoredFiles == 0)
                        {
                            if (bestScore.Key < 0.75)
                            {
                                bestScore = KeyValuePair.Create(0.75, $"Package is a subset of the source repository contents, with no ignored {filesString}.");
                            }
                        }
                        else
                        {
                            if (bestScore.Key < 0.65)
                            {
                                bestScore = KeyValuePair.Create(0.65, $"Package is a subset of the source repository contents, with {result.NumIgnoredFiles} ignored {filesString}.");
                            }
                        }
                    }
                }
                else if (string.Equals(result.StrategyName, "AutoBuildProducesSamePackage"))
                {
                    if (result.IsSuccess && !result.IsError)
                    {
                        if (result.NumIgnoredFiles == 0)
                        {
                            if (bestScore.Key < 0.90)
                            {
                                bestScore = KeyValuePair.Create(0.90, $"Package was re-built from source, with no ignored {filesString}.");
                            }
                        }
                        else
                        {
                            if (bestScore.Key < 0.65)
                            {
                                bestScore = KeyValuePair.Create(0.65, $"Package was re-built from source, with {result.NumIgnoredFiles} ignored {filesString}.");
                            }
                        }
                    }
                }
                else if (string.Equals(result.StrategyName, "OryxBuildStrategy"))
                {
                    if (result.IsSuccess && !result.IsError)
                    {
                        if (result.NumIgnoredFiles == 0)
                        {
                            if (bestScore.Key < 0.90)
                            {
                                bestScore = KeyValuePair.Create(0.90, $"Package was re-built from source, with no ignored {filesString}.");
                            }
                        }
                        else
                        {
                            if (bestScore.Key < 0.65)
                            {
                                bestScore = KeyValuePair.Create(0.65, $"Package was re-built from source, with {result.NumIgnoredFiles} ignored {filesString}.");
                            }
                        }
                    }
                }
            }
            return bestScore;
        }

        public override async Task<ErrorCode> RunAsync(ReproducibleToolOptions reproducibleToolOptions)
        {
            if (reproducibleToolOptions.ShowAllDifferences)
            {
                reproducibleToolOptions.ShowDifferences = true;
            }

            // Validate strategies (before we do any other processing
            IEnumerable<Type>? runSpecificStrategies = null;
            if (reproducibleToolOptions.SpecificStrategies != null)
            {
                IEnumerable<string>? requestedStrategies = reproducibleToolOptions.SpecificStrategies.Split(',').Select(s => s.Trim().ToLowerInvariant()).Distinct();
                runSpecificStrategies = typeof(BaseStrategy).Assembly.GetTypes()
                    .Where(t => t.IsSubclassOf(typeof(BaseStrategy)))
                    .Where(t => requestedStrategies.Contains(t.Name.ToLowerInvariant()));
                Logger.Debug("Specific strategies requested: {0}", string.Join(", ", runSpecificStrategies.Select(t => t.Name)));

                if (requestedStrategies.Count() != runSpecificStrategies.Count())
                {
                    Logger.Debug("Invalid strategies.");
                    Console.WriteLine("Invalid strategy, available options are:");
                    IEnumerable<Type>? allStrategies = typeof(BaseStrategy).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(BaseStrategy)));
                    foreach (Type? s in allStrategies)
                    {
                        Console.WriteLine($" * {s.Name}");
                    }
                    Console.WriteLine("Example: oss-reproducible --specific-strategies AutoBuildProducesSamePackage,PackageMatchesSourceStrategy pkg:npm/left-pad@1.3.0");
                    return ErrorCode.ErrorParsingOptions;
                }
            }

            // Expand targets
            List<string>? targets = new List<string>();
            foreach (string? target in reproducibleToolOptions.Targets ?? Array.Empty<string>())
            {
                PackageURL? purl = new PackageURL(target);
                PackageDownloader? downloader = new PackageDownloader(purl, ProjectManagerFactory, "temp");
                foreach (PackageURL? version in downloader.PackageVersions)
                {
                    targets.Add(version.ToString());
                }
            }
            List<ReproducibleToolResult>? finalResults = new List<ReproducibleToolResult>();
            ErrorCode exitCode = ErrorCode.Ok;

            foreach (string? target in targets)
            {
                string? tempDirectoryName = null;
                try
                {
                    Console.WriteLine($"Analyzing {target}...");
                    Logger.Debug("Processing: {0}", target);

                    PackageURL? purl = new PackageURL(target);
                    if (purl.Version == null)
                    {
                        Logger.Error("Package is missing a version, which is required for this tool.");
                        continue;
                    }

                    tempDirectoryName = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString().Substring(0, 8));
                    FileSystemHelper.RetryDeleteDirectory(tempDirectoryName);
                    // Download the package
                    Console.WriteLine("Downloading package...");
                    PackageDownloader? packageDownloader = new PackageDownloader(purl, ProjectManagerFactory, Path.Join(tempDirectoryName, "package"));
                    List<string>? downloadResults = await packageDownloader.DownloadPackageLocalCopy(purl, false, true);

                    if (!downloadResults.Any())
                    {
                        Logger.Debug("Unable to download package.");
                    }

                    // Locate the source
                    Console.WriteLine("Locating source...");
                    string sourceDirectory = Path.Join(tempDirectoryName, "src");
                    FindSourceTool? findSourceTool = new FindSourceTool(ProjectManagerFactory);
                    Dictionary<PackageURL, double>? sourceMap = await findSourceTool.FindSourceAsync(purl);
                    if (sourceMap.Any())
                    {
                        List<KeyValuePair<PackageURL, double>>? sourceMapList = sourceMap.ToList();
                        sourceMapList.Sort((a, b) => a.Value.CompareTo(b.Value));
                        PackageURL? bestSourcePurl = sourceMapList.Last().Key;
                        Logger.Debug("Identified best source code repository: {0}", bestSourcePurl);

                        // Download the source
                        Console.WriteLine("Downloading source...");
                        foreach (string reference in GetSourceReferences(
                            bestSourcePurl.Version,
                            reproducibleToolOptions.OverrideSourceReference))
                        {
                            Logger.Debug("Trying to download package, version/reference [{0}].", reference);
                            PackageURL? purlRef = new PackageURL(bestSourcePurl.Type, bestSourcePurl.Namespace, bestSourcePurl.Name, reference, bestSourcePurl.Qualifiers, bestSourcePurl.Subpath);
                            packageDownloader = new PackageDownloader(purlRef, ProjectManagerFactory, Path.Join(tempDirectoryName, "src"));
                            downloadResults = await packageDownloader.DownloadPackageLocalCopy(purlRef, false, true);
                            if (downloadResults.Any())
                            {
                                if (!string.IsNullOrEmpty(bestSourcePurl.Subpath))
                                {
                                    if (downloadResults.Count != 1)
                                    {
                                        throw new InvalidDataException("Cannot resolve a repository subpath across multiple source downloads.");
                                    }

                                    string repositoryRoot = GetSourceRepositoryRoot(downloadResults[0], bestSourcePurl.Type);
                                    sourceDirectory = ResolveSourceDirectory(repositoryRoot, bestSourcePurl.Subpath);
                                }
                                break;
                            }
                        }
                        if (!downloadResults.Any())
                        {
                            Logger.Debug("Unable to download source.");
                        }
                    }
                    else
                    {
                        Logger.Debug("Unable to locate source repository.");
                    }

                    // Execute all available strategies
                    StrategyOptions? strategyOptions = new StrategyOptions()
                    {
                        PackageDirectory = Path.Join(tempDirectoryName, "package"),
                        SourceDirectory = sourceDirectory,
                        PackageUrl = purl,
                        TemporaryDirectory = Path.GetFullPath(tempDirectoryName),
                        DiffTechnique = reproducibleToolOptions.DiffTechnique
                    };

                    // First, check to see how many strategies apply
                    IEnumerable<Type>? strategies = runSpecificStrategies;
                    if (strategies == null || !strategies.Any())
                    {
                        strategies = BaseStrategy.GetStrategies(strategyOptions) ?? Array.Empty<Type>();
                    }

                    int numStrategiesApplies = 0;
                    foreach (Type? strategy in strategies)
                    {
                        ConstructorInfo? ctor = strategy.GetConstructor(new Type[] { typeof(StrategyOptions) });
                        if (ctor != null)
                        {
                            BaseStrategy? strategyObject = (BaseStrategy)(ctor.Invoke(new object?[] { strategyOptions }));
                            if (strategyObject.StrategyApplies())
                            {
                                numStrategiesApplies++;
                            }
                        }
                    }

                    Console.Write($"Out of {Yellow(strategies.Count().ToString())} potential strategies, {Yellow(numStrategiesApplies.ToString())} apply. ");
                    if (reproducibleToolOptions.AllStrategies)
                    {
                        Console.WriteLine("Analysis will continue even after a successful strategy is found.");
                    }
                    else
                    {
                        Console.WriteLine("Analysis will stop after the first successful strategy is found.");
                    }
                    List<StrategyResult> strategyResults = new List<StrategyResult>();

                    Console.WriteLine($"\n{Blue("Results: ")}");

                    bool hasSuccessfulStrategy = false;

                    foreach (Type? strategy in strategies)
                    {
                        ConstructorInfo? ctor = strategy.GetConstructor(new Type[] { typeof(StrategyOptions) });
                        if (ctor != null)
                        {
                            // Create a temporary directory, copy the contents from source/package
                            // so that this strategy can modify the contents without affecting other strategies.
                            StrategyOptions? tempStrategyOptions = new StrategyOptions
                            {
                                PackageDirectory = Path.Join(strategyOptions.TemporaryDirectory, strategy.Name, "package"),
                                SourceDirectory = Path.Join(strategyOptions.TemporaryDirectory, strategy.Name, "src"),
                                TemporaryDirectory = Path.Join(strategyOptions.TemporaryDirectory, strategy.Name),
                                PackageUrl = strategyOptions.PackageUrl,
                                IncludeDiffoscope = reproducibleToolOptions.ShowDifferences
                            };

                            try
                            {
                                OssReproducibleHelpers.DirectoryCopy(strategyOptions.PackageDirectory, tempStrategyOptions.PackageDirectory);
                                OssReproducibleHelpers.DirectoryCopy(strategyOptions.SourceDirectory, tempStrategyOptions.SourceDirectory);
                            }
                            catch (Exception ex)
                            {
                                Logger.Debug(ex, "Error copying directory for strategy. Aborting execution.");
                            }

                            System.IO.Directory.CreateDirectory(tempStrategyOptions.PackageDirectory);
                            System.IO.Directory.CreateDirectory(tempStrategyOptions.SourceDirectory);

                            try
                            {
                                BaseStrategy? strategyObject = (BaseStrategy)(ctor.Invoke(new object?[] { tempStrategyOptions }));
                                StrategyResult? strategyResult = strategyObject.Execute();

                                if (strategyResult != null)
                                {
                                    strategyResults.Add(strategyResult);
                                }

                                if (strategyResult != null)
                                {
                                    if (strategyResult.IsSuccess)
                                    {
                                        Console.WriteLine($" [{Bold().Yellow("PASS")}] {Yellow(strategy.Name)}");
                                        hasSuccessfulStrategy = true;
                                    }
                                    else
                                    {
                                        Console.WriteLine($" [{Red("FAIL")}] {Red(strategy.Name)}");
                                    }

                                    if (reproducibleToolOptions.ShowDifferences)
                                    {
                                        foreach (StrategyResultMessage? resultMessage in strategyResult.Messages)
                                        {
                                            if (resultMessage.Filename != null && resultMessage.CompareFilename != null)
                                            {
                                                Console.WriteLine($"  {Bright.Black("(")}{Blue("P ")}{Bright.Black(")")} {resultMessage.Filename}");
                                                Console.WriteLine($"  {Bright.Black("(")}{Blue(" S")}{Bright.Black(")")} {resultMessage.CompareFilename}");
                                            }
                                            else if (resultMessage.Filename != null)
                                            {
                                                Console.WriteLine($"  {Bright.Black("(")}{Blue("P+")}{Bright.Black(")")} {resultMessage.Filename}");
                                            }
                                            else if (resultMessage.CompareFilename != null)
                                            {
                                                Console.WriteLine($"  {Bright.Black("(")}{Blue("S+")}{Bright.Black(")")} {resultMessage.CompareFilename}");
                                            }

                                            IEnumerable<DiffPiece>? differences = resultMessage.Differences ?? Array.Empty<DiffPiece>();

                                            int maxShowDifferences = 20;
                                            int numShowDifferences = 0;

                                            foreach (DiffPiece? diff in differences)
                                            {
                                                if (!reproducibleToolOptions.ShowAllDifferences && numShowDifferences > maxShowDifferences)
                                                {
                                                    Console.WriteLine(Background.Blue(Bold().White("NOTE: Additional differences exist but are not shown. Pass --show-all-differences to view them all.")));
                                                    break;
                                                }

                                                switch (diff.Type)
                                                {
                                                    case ChangeType.Inserted:
                                                        Console.WriteLine($"{Bright.Black(diff.Position + ")")}\t{Red("+")} {Blue(diff.Text)}");
                                                        ++numShowDifferences;
                                                        break;

                                                    case ChangeType.Deleted:
                                                        Console.WriteLine($"\t{Green("-")} {Green(diff.Text)}");
                                                        ++numShowDifferences;
                                                        break;

                                                    default:
                                                        break;
                                                }
                                            }
                                            if (numShowDifferences > 0)
                                            {
                                                Console.WriteLine();
                                            }
                                        }

                                        string? diffoscopeFile = Guid.NewGuid().ToString() + ".html";
                                        File.WriteAllText(diffoscopeFile, strategyResult.Diffoscope);
                                        Console.WriteLine($"  Diffoscope results written to {diffoscopeFile}.");
                                    }
                                }
                                else
                                {
                                    Console.WriteLine(Green($"  [-] {strategy.Name}"));
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn(ex, "Error processing {0}: {1}", strategy, ex.Message);
                                Logger.Debug(ex.StackTrace);
                            }
                        }

                        if (hasSuccessfulStrategy && !reproducibleToolOptions.AllStrategies)
                        {
                            break;  // We don't need to continue
                        }
                    }

                    ReproducibleToolResult? reproducibilityToolResult = new ReproducibleToolResult
                    {
                        PackageUrl = purl.ToString(),
                        Results = strategyResults
                    };

                    finalResults.Add(reproducibilityToolResult);

                    (double score, string scoreText) = GetReproducibilityScore(reproducibilityToolResult);
                    Console.WriteLine($"\n{Blue("Summary:")}");
                    string? scoreDisplay = $"{(score * 100.0):0.#}";
                    if (reproducibilityToolResult.IsReproducible)
                    {
                        Console.WriteLine($"  [{Yellow(scoreDisplay + "%")}] {Yellow(scoreText)}");
                    }
                    else
                    {
                        Console.WriteLine($"  [{Red(scoreDisplay + "%")}] {Red(scoreText)}");
                    }
                }
                catch (Exception ex)
                {
                    exitCode = ErrorCode.ProcessingException;
                    Logger.Warn(ex, "Error processing {0}: {1}", target, ex.Message);
                    Logger.Debug(ex.StackTrace);
                }
                finally
                {
                    if (tempDirectoryName != null)
                    {
                        if (reproducibleToolOptions.LeaveIntermediateFiles)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"Intermediate files are located in [{tempDirectoryName}].");
                        }
                        else
                        {
                            FileSystemHelper.RetryDeleteDirectory(tempDirectoryName);
                        }
                    }
                }
            }

            if (finalResults.Any())
            {
                // Write the output somewhere
                string? jsonResults = Newtonsoft.Json.JsonConvert.SerializeObject(finalResults, Newtonsoft.Json.Formatting.Indented);
                if (!string.IsNullOrWhiteSpace(reproducibleToolOptions.OutputFile) && !string.Equals(reproducibleToolOptions.OutputFile, "-", StringComparison.InvariantCultureIgnoreCase))
                {
                    try
                    {
                        File.WriteAllText(reproducibleToolOptions.OutputFile, jsonResults);
                        Console.WriteLine($"Detailed results are available in {reproducibleToolOptions.OutputFile}.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex, "Unable to write to {0}. Writing to console instead.", reproducibleToolOptions.OutputFile);
                        Console.WriteLine(jsonResults);
                    }
                }
                else if (string.Equals(reproducibleToolOptions.OutputFile, "-", StringComparison.InvariantCultureIgnoreCase))
                {
                    Console.WriteLine(jsonResults);
                }
            }
            else
            {
                Logger.Debug("No results were produced.");
            }

            return exitCode;
        }

        internal static IReadOnlyList<string> GetSourceReferences(
            string? sourceReference,
            string? fallbackSourceReference)
        {
            // A registry version is not evidence of a git ref, even when their names happen to match.
            return new[]
                {
                    sourceReference,
                    fallbackSourceReference,
                    "master",
                    "main"
                }
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Select(reference => reference!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        internal static string GetSourceRepositoryRoot(string extractionDirectory, string sourceType)
        {
            if (!string.Equals(sourceType, "github", StringComparison.OrdinalIgnoreCase))
            {
                return extractionDirectory;
            }

            // GitHub archives add one wrapper; recursive unwrapping would consume real monorepo directories.
            string[] entries = System.IO.Directory.GetFileSystemEntries(extractionDirectory);
            if (entries.Length != 1 || !System.IO.Directory.Exists(entries[0]))
            {
                throw new InvalidDataException("Expected a single repository root in the downloaded GitHub archive.");
            }

            return entries[0];
        }

        internal static string ResolveSourceDirectory(string repositoryRoot, string? repositorySubpath)
        {
            if (string.IsNullOrEmpty(repositorySubpath))
            {
                return repositoryRoot;
            }

            string normalizedSubpath = repositorySubpath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedSubpath) ||
                normalizedSubpath.Split(Path.DirectorySeparatorChar).Any(segment => segment is "." or ".."))
            {
                throw new ArgumentException("Repository subpath must be relative and cannot contain traversal segments.", nameof(repositorySubpath));
            }

            string candidate = Path.GetFullPath(Path.Combine(repositoryRoot, normalizedSubpath));
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)) + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            if (!candidate.StartsWith(root, comparison))
            {
                throw new ArgumentException("Repository subpath must stay within the repository root.", nameof(repositorySubpath));
            }

            // Falling back to the whole repository could report a match using a different package's files.
            if (!System.IO.Directory.Exists(candidate))
            {
                throw new DirectoryNotFoundException($"Repository subpath [{repositorySubpath}] was not found in the downloaded source.");
            }

            return candidate;
        }
    }
}
