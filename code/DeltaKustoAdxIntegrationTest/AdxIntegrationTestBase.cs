﻿using DeltaKustoFileIntegrationTest;
using DeltaKustoIntegration.Database;
using DeltaKustoIntegration.Kusto;
using DeltaKustoIntegration.Parameterization;
using DeltaKustoLib;
using DeltaKustoLib.CommandModel;
using DeltaKustoLib.KustoModel;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace DeltaKustoAdxIntegrationTest
{
    public abstract class AdxIntegrationTestBase : IntegrationTestBase
    {
        private readonly bool _overrideLoginTokenProvider;

        protected AdxIntegrationTestBase(bool overrideLoginTokenProvider = true)
        {
            var clusterUri = Environment.GetEnvironmentVariable("deltaKustoClusterUri");
            var tenantId = Environment.GetEnvironmentVariable("deltaKustoTenantId");
            var servicePrincipalId = Environment.GetEnvironmentVariable("deltaKustoSpId");
            var servicePrincipalSecret = Environment.GetEnvironmentVariable("deltaKustoSpSecret");

            if (string.IsNullOrWhiteSpace(clusterUri))
            {
                throw new ArgumentNullException(nameof(clusterUri));
            }
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                throw new ArgumentNullException(nameof(tenantId));
            }
            if (string.IsNullOrWhiteSpace(servicePrincipalId))
            {
                throw new ArgumentNullException(nameof(servicePrincipalId));
            }
            if (string.IsNullOrWhiteSpace(servicePrincipalSecret))
            {
                throw new ArgumentNullException(nameof(servicePrincipalSecret));
            }

            _overrideLoginTokenProvider = overrideLoginTokenProvider;
            ClusterUri = new Uri(clusterUri);
            TenantId = tenantId;
            ServicePrincipalId = servicePrincipalId;
            ServicePrincipalSecret = servicePrincipalSecret;
            KustoGatewayFactory = new KustoManagementGatewayFactory(
                new TokenProviderParameterization
                {
                    Login = new ServicePrincipalLoginParameterization
                    {
                        TenantId = TenantId,
                        ClientId = ServicePrincipalId,
                        Secret = ServicePrincipalSecret
                    }
                },
                Tracer,
                "test",
                null);
        }

        protected Uri ClusterUri { get; }

        protected string TenantId { get; }

        protected string ServicePrincipalId { get; }

        protected string ServicePrincipalSecret { get; }

        protected IKustoManagementGatewayFactory KustoGatewayFactory { get; }

        protected async Task<string> InitializeDbAsync()
        {
            var dbName = await AdxDbTestHelper.Instance.GetCleanDbAsync();

            return dbName;
        }

        protected async Task TestAdxToFile(string statesFolderPath)
        {
            await LoopThroughStateFilesAsync(
                Path.Combine(statesFolderPath, "States"),
                async (fromFile, toFile) =>
                {
                    var testDbName = await InitializeDbAsync();
                    var currentDbName = await InitializeDbAsync();

                    await PrepareDbAsync(fromFile, currentDbName);

                    var outputPath = Path.Combine("outputs", statesFolderPath, "adx-to-file/")
                        + Path.GetFileNameWithoutExtension(fromFile)
                        + "_2_"
                        + Path.GetFileNameWithoutExtension(toFile)
                        + ".kql";
                    var overrides = ImmutableArray<(string path, string value)>
                    .Empty
                    .Add(("jobs.main.current.adx.clusterUri", ClusterUri.ToString()))
                    .Add(("jobs.main.current.adx.database", currentDbName))
                    .Add(("jobs.main.target.scripts[0].filePath", toFile))
                    .Add(("jobs.main.action.filePath", outputPath));
                    var parameters = await RunParametersAsync(
                        "adx-to-file-params.json",
                        overrides);
                    var outputCommands = await LoadScriptAsync("", outputPath);
                    var targetFileCommands = CommandBase.FromScript(
                        await File.ReadAllTextAsync(toFile));

                    await Task.WhenAll(
                        ApplyCommandsAsync(outputCommands, currentDbName),
                        ApplyCommandsAsync(targetFileCommands, testDbName));

                    var finalCommandsTask = FetchDbCommandsAsync(currentDbName);
                    var targetAdxCommands = await FetchDbCommandsAsync(testDbName);
                    var finalCommands = await finalCommandsTask;
                    var targetModel = DatabaseModel.FromCommands(targetAdxCommands);
                    var finalModel = DatabaseModel.FromCommands(finalCommands);
                    var finalScript = string.Join(";\n\n", finalCommands.Select(c => c.ToScript()));
                    var targetScript = string.Join(";\n\n", targetFileCommands.Select(c => c.ToScript()));

                    Assert.True(
                        finalModel.Equals(targetModel),
                        $"From {fromFile} to {toFile}:\n\n{finalScript}\nvs\n\n{targetScript}");
                    AdxDbTestHelper.Instance.ReleaseDb(currentDbName);
                });
        }

        protected async Task TestFileToAdx(string statesFolderPath)
        {
            await LoopThroughStateFilesAsync(
                Path.Combine(statesFolderPath, "States"),
                async (fromFile, toFile) =>
                {
                    var testDbName = await InitializeDbAsync();
                    var targetDbName = await InitializeDbAsync();

                    await PrepareDbAsync(toFile, targetDbName);

                    var outputPath = Path.Combine("outputs", statesFolderPath, "file-to-adx/")
                        + Path.GetFileNameWithoutExtension(fromFile)
                        + "_2_"
                        + Path.GetFileNameWithoutExtension(toFile)
                        + ".kql";
                    var overrides = ImmutableArray<(string path, string value)>
                        .Empty
                        .Add(("jobs.main.target.adx.clusterUri", ClusterUri.ToString()))
                        .Add(("jobs.main.target.adx.database", targetDbName))
                        .Append(("jobs.main.current.scripts[0].filePath", fromFile))
                        .Append(("jobs.main.action.filePath", outputPath));
                    var parameters = await RunParametersAsync(
                        "file-to-adx-params.json",
                        overrides);
                    var outputCommands = await LoadScriptAsync("", outputPath);
                    var currentCommands = CommandBase.FromScript(
                        await File.ReadAllTextAsync(fromFile));
                    var targetCommandsTask = FetchDbCommandsAsync(targetDbName);

                    await ApplyCommandsAsync(currentCommands.Concat(outputCommands), testDbName);

                    var targetCommands = await targetCommandsTask;
                    var finalCommands = await FetchDbCommandsAsync(testDbName);
                    var targetModel = DatabaseModel.FromCommands(targetCommands);
                    var finalModel = DatabaseModel.FromCommands(finalCommands);
                    var finalScript = string.Join(";\n\n", finalCommands.Select(c => c.ToScript()));
                    var targetScript = string.Join(";\n\n", targetCommands.Select(c => c.ToScript()));

                    Assert.True(
                        finalModel.Equals(targetModel),
                        $"From {fromFile} to {toFile}:\n\n{finalScript}\nvs\n\n{targetScript}");
                    AdxDbTestHelper.Instance.ReleaseDb(targetDbName);
                    AdxDbTestHelper.Instance.ReleaseDb(testDbName);
                });
        }

        protected async Task TestAdxToAdx(string statesFolderPath)
        {
            await LoopThroughStateFilesAsync(
                Path.Combine(statesFolderPath, "States"),
                async (fromFile, toFile) =>
                {
                    var currentDbName = await InitializeDbAsync();
                    var targetDbName = await InitializeDbAsync();

                    await Task.WhenAll(
                        PrepareDbAsync(fromFile, currentDbName),
                        PrepareDbAsync(toFile, targetDbName));

                    var outputPath = Path.Combine("outputs", statesFolderPath, "adx-to-adx/")
                        + Path.GetFileNameWithoutExtension(fromFile)
                        + "_2_"
                        + Path.GetFileNameWithoutExtension(toFile)
                        + ".kql";
                    var overrides = ImmutableArray<(string path, string value)>
                    .Empty
                    .Add(("jobs.main.current.adx.clusterUri", ClusterUri.ToString()))
                    .Add(("jobs.main.current.adx.database", currentDbName))
                    .Add(("jobs.main.target.adx.clusterUri", ClusterUri.ToString()))
                    .Add(("jobs.main.target.adx.database", targetDbName))
                    .Add(("jobs.main.action.filePath", outputPath));
                    var parameters = await RunParametersAsync(
                        "adx-to-adx-params.json",
                        overrides);
                    var targetCommandsTask = FetchDbCommandsAsync(targetDbName);
                    var finalCommands = await FetchDbCommandsAsync(currentDbName);
                    var targetCommands = await targetCommandsTask;
                    var targetModel = DatabaseModel.FromCommands(targetCommands);
                    var finalModel = DatabaseModel.FromCommands(finalCommands);
                    var finalScript = string.Join(";\n\n", finalCommands.Select(c => c.ToScript()));
                    var targetScript = string.Join(";\n\n", targetCommands.Select(c => c.ToScript()));

                    Assert.True(
                        targetModel.Equals(finalModel),
                        $"From {fromFile} to {toFile}:\n\n{finalScript}\nvs\n\n{targetScript}");
                    AdxDbTestHelper.Instance.ReleaseDb(currentDbName);
                    AdxDbTestHelper.Instance.ReleaseDb(targetDbName);
                });
        }

        private async Task LoopThroughStateFilesAsync(
            string folderPath,
            Func<string, string, Task> loopFunction)
        {
            var stateFiles = Directory.GetFiles(folderPath);

            foreach (var fromFile in stateFiles)
            {
                foreach (var toFile in stateFiles)
                {
                    await loopFunction(fromFile, toFile);
                }
            }
        }

        private async Task ApplyCommandsAsync(IEnumerable<CommandBase> commands, string dbName)
        {
            var gateway = KustoGatewayFactory.CreateGateway(ClusterUri, dbName);

            //  Apply commands to the db
            await gateway.ExecuteCommandsAsync(commands);
        }

        private async Task<IImmutableList<CommandBase>> FetchDbCommandsAsync(string dbName)
        {
            var gateway = KustoGatewayFactory.CreateGateway(ClusterUri, dbName);
            var dbProvider = (IDatabaseProvider)new KustoDatabaseProvider(
                new ConsoleTracer(false),
                gateway);
            var emptyProvider = (IDatabaseProvider)new EmptyDatabaseProvider();
            var finalDb = await dbProvider.RetrieveDatabaseAsync();
            var emptyDb = await emptyProvider.RetrieveDatabaseAsync();
            //  Use the delta from an empty db to get 
            var finalCommands = emptyDb.ComputeDelta(finalDb);

            return finalCommands;
        }

        protected override Task<MainParameterization> RunParametersAsync(
            string parameterFilePath,
            IEnumerable<(string path, string value)>? overrides = null)
        {
            var adjustedOverrides = overrides != null
                ? overrides
                : ImmutableList<(string path, string value)>.Empty;

            if (_overrideLoginTokenProvider)
            {
                adjustedOverrides = adjustedOverrides
                    .Append(("tokenProvider.login.tenantId", TenantId))
                    .Append(("tokenProvider.login.clientId", ServicePrincipalId))
                    .Append(("tokenProvider.login.secret", ServicePrincipalSecret));
            }

            return base.RunParametersAsync(parameterFilePath, adjustedOverrides);
        }

        protected async Task PrepareDbAsync(string scriptPath, string dbName)
        {
            var script = await File.ReadAllTextAsync(scriptPath);

            try
            {
                var commands = CommandBase.FromScript(script);
                var gateway = KustoGatewayFactory.CreateGateway(ClusterUri, dbName);

                await gateway.ExecuteCommandsAsync(commands);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failure during PrepareDb.  dbName='{dbName}'.  "
                    + $"Script path = '{scriptPath}'.  "
                    + $"Script = '{script.Replace("\n", "\\n").Replace("\r", "\\r")}'",
                    ex);
            }
        }
    }
}