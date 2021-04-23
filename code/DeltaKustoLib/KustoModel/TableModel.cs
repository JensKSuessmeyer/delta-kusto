﻿using DeltaKustoLib.CommandModel;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeltaKustoLib.KustoModel
{
    public class TableModel
    {
        public EntityName TableName { get; }

        public IImmutableList<ColumnModel> Columns { get; }

        public QuotedText? Folder { get; }

        public QuotedText? DocString { get; }

        private TableModel(
            EntityName tableName,
            IEnumerable<ColumnModel> columns,
            QuotedText? folder,
            QuotedText? docString)
        {
            TableName = tableName;
            Columns = columns.ToImmutableArray();
            Folder = folder;
            DocString = docString;
        }

        #region Object methods
        public override bool Equals(object? obj)
        {
            var other = obj as TableModel;

            return other != null
                && other.TableName.Equals(TableName)
                && other.Columns.OrderBy(c => c.ColumnName).SequenceEqual(
                    Columns.OrderBy(c => c.ColumnName))
                && object.Equals(other.Folder, Folder)
                && object.Equals(other.DocString, DocString);
        }

        public override int GetHashCode()
        {
            return TableName.Name.GetHashCode()
                ^ Columns.Aggregate(0, (h, c) => h ^ c.GetHashCode())
                ^ (Folder == null ? 0 : Folder.Text.GetHashCode())
                ^ (DocString == null ? 0 : DocString.Text.GetHashCode());
        }
        #endregion

        internal static IImmutableList<TableModel> FromCommands(
            IImmutableList<CreateTableCommand> createTables,
            IImmutableList<AlterMergeTableColumnDocStringsCommand> alterMergeTableColumns)
        {
            var tableDocStringColumnMap = alterMergeTableColumns
                .GroupBy(c => c.TableName)
                .ToImmutableDictionary(g => g.Key, g => g.SelectMany(c => c.Columns));
            var tables = createTables
                .Select(ct => new TableModel(
                    ct.TableName,
                    FromCodeColumn(
                        ct.Columns,
                        tableDocStringColumnMap.ContainsKey(ct.TableName)
                        ? tableDocStringColumnMap[ct.TableName]
                        : null),
                    ct.Folder,
                    ct.DocString))
                .ToImmutableArray();

            return tables;
        }

        internal static IEnumerable<CommandBase> ComputeDelta(
            IImmutableList<TableModel> currentModels,
            IImmutableList<TableModel> targetModels)
        {
            var currentTables = currentModels.ToImmutableDictionary(m => m.TableName);
            var currentTableNames = currentTables.Keys.ToImmutableSortedSet();
            var targetTables = targetModels.ToImmutableDictionary(m => m.TableName);
            var targetTableNames = targetTables.Keys.ToImmutableSortedSet();
            var dropTableNames = currentTableNames.Except(targetTableNames);
            var createTableNames = targetTableNames.Except(currentTableNames);
            var modifiedTableNames = targetTableNames
                .Intersect(currentTableNames)
                .Where(name => !targetTables[name].Equals(currentTables[name]));
            var dropTables = dropTableNames
                .Select(name => new DropTableCommand(name) as CommandBase);
            var createTables = createTableNames
                .Select(name => targetTables[name].ToCreateTable() as CommandBase);
            var modifiedTables = modifiedTableNames
                .SelectMany(name => currentTables[name].ComputeDelta(targetTables[name]));

            return dropTables
                .Concat(createTables)
                .Concat(modifiedTables);
        }

        private static IEnumerable<ColumnModel> FromCodeColumn(
            IImmutableList<TableColumn> codeColumns,
            IEnumerable<AlterMergeTableColumnDocStringsCommand.ColumnDocString>? columnDocStrings)
        {
            var columnDocMap = columnDocStrings != null
                ? columnDocStrings.ToImmutableDictionary(c => c.ColumnName, c => c.DocString)
                : ImmutableDictionary<EntityName, QuotedText>.Empty;
            var columns = codeColumns
                .Select(c => new ColumnModel(
                    c.ColumnName,
                    c.PrimitiveType,
                    columnDocMap.ContainsKey(c.ColumnName) ? columnDocMap[c.ColumnName] : null));

            return columns.ToImmutableArray();
        }

        private CreateTableCommand ToCreateTable()
        {
            return new CreateTableCommand(
                TableName,
                Columns.Select(c => new TableColumn(c.ColumnName, c.PrimitiveType)),
                Folder,
                DocString);
        }

        private IEnumerable<CommandBase> ComputeDelta(TableModel targetModel)
        {
            return new[]
            {
                targetModel.ToCreateTable()
            };
        }
    }
}