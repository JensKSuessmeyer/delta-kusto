﻿using Kusto.Language;
using Kusto.Language.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

namespace DeltaKustoLib.CommandModel
{
    public abstract class CommandBase : IEquatable<CommandBase>
    {
        public static IImmutableList<CommandBase> FromScript(string script)
        {
            var scripts = SplitCommandScripts(script);
            var commands = scripts
                .Select(s => ParseAndCreateCommand(s))
                .ToImmutableArray();

            return commands;
        }

        public abstract string CommandFriendlyName { get; }

        public abstract bool Equals([AllowNull] CommandBase other);

        public abstract string ToScript();

        #region Object methods
        public override string ToString()
        {
            return ToScript();
        }

        public override bool Equals(object? obj)
        {
            var command = obj as CommandBase;

            return command != null && this.Equals(command);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
        #endregion

        private static CommandBase ParseAndCreateCommand(string script)
        {
            try
            {
                var code = KustoCode.Parse(script);
                var command = CreateCommand(script, code);

                return command;
            }
            catch (Exception ex)
            {
                throw new DeltaException(
                    $"Issue parsing script",
                    script,
                    ex);
            }
        }

        private static CommandBase CreateCommand(string script, KustoCode code)
        {
            var commandBlock = code.Syntax as CommandBlock;

            if (commandBlock == null)
            {
                throw new DeltaException("Script isn't a command");
            }

            var unknownCommand = commandBlock.GetDescendants<UnknownCommand>().FirstOrDefault();

            if (unknownCommand != null)
            {
                return RerouteUnknownCommand(script, unknownCommand);
            }
            else
            {
                var customCommand = commandBlock.GetUniqueDescendant<CustomCommand>("custom command");

                switch (customCommand.CommandKind)
                {
                    case "CreateFunction":
                    case "CreateOrAlterFunction":
                        return CreateFunctionCommand.FromCode(commandBlock);
                    case "DropFunction":
                        return DropFunctionCommand.FromCode(commandBlock);
                    case "DropFunctions":
                        return DropFunctionsCommand.FromCode(commandBlock);
                    case "CreateTable":
                        return CreateTableCommand.FromCode(commandBlock);
                    case "CreateMergeTable":
                        //  We need to do this since the parsing is quite different with the with-node
                        //  between a .create and .create-merge (for unknown reasons)
                        return ParseAndCreateCommand(
                            ReplaceFirstOccurence(script, "create-merge", "create"));
                    case "AlterMergeTable":
                        return ParseAndCreateCommand(
                            ReplaceFirstOccurence(script, "alter-merge", "create"));
                    case "CreateTables":
                        return CreateTablesCommand.FromCode(commandBlock);
                    case "DropTable":
                        return DropTableCommand.FromCode(commandBlock);
                    case "DropTables":
                        return DropTablesCommand.FromCode(commandBlock);
                    case "AlterMergeTableColumnDocStrings":
                        return AlterMergeTableColumnDocStringsCommand.FromCode(commandBlock);
                    case "DropTableColumns":
                        return DropTableColumnsCommand.FromCode(commandBlock);

                    default:
                        throw new DeltaException(
                            $"Can't handle CommandKind '{customCommand.CommandKind}'");
                }
            }
        }

        private static CommandBase RerouteUnknownCommand(
            string script,
            UnknownCommand unknownCommand)
        {
            //  .create merge tables isn't a recognized command by the parser (for some reason)
            if (unknownCommand.Parts[0].Kind == SyntaxKind.CreateMergeKeyword
                && unknownCommand.Parts[1].Kind == SyntaxKind.TablesKeyword)
            {
                var cutPoint = unknownCommand.Parts[1].TextStart + unknownCommand.Parts[1].FullWidth;
                var newScript = ".create tables " + script.Substring(cutPoint);

                return ParseAndCreateCommand(newScript);
            }
            else
            {
                throw new DeltaException("Unrecognized command");
            }
        }

        private static string ReplaceFirstOccurence(string script, string oldValue, string newValue)
        {
            var occurenceIndex = script.IndexOf(oldValue);

            if (occurenceIndex == -1)
            {
                throw new InvalidOperationException(
                    $"Script '{script}' should contain '{oldValue}'");
            }

            var newScript = script.Substring(0, occurenceIndex)
                + newValue
                + script.Substring(occurenceIndex + oldValue.Length);

            return newScript;
        }

        private static IEnumerable<string> SplitCommandScripts(string script)
        {
            var lines = script
                .Split('\n')
                .Select(l => l.Trim())
                //  Remove comment lines
                .Where(l => !l.StartsWith("//"));
            var currentCommandLines = new List<string>();

            foreach (var line in lines)
            {
                if (line == string.Empty)
                {
                    if (currentCommandLines.Any())
                    {
                        yield return string.Join('\n', currentCommandLines);
                        currentCommandLines.Clear();
                    }
                }
                else
                {
                    currentCommandLines.Add(line);
                }
            }

            if (currentCommandLines.Any())
            {
                yield return string.Join('\n', currentCommandLines);
            }
        }
    }
}