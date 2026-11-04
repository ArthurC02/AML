using System.Text.Json;
using Aml.Core.Cases;
using Aml.Core.Ports;
using Aml.Core.Retrieval;
using Aml.Core.Screening;
using Aml.Infra.Html;
using Aml.Infra.Jobs;
using Aml.Infra.Persistence;
using Aml.Infra.Persistence.Repositories;
using Aml.Infra.Txt;
using Aml.Services;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Aml.Tests.Integration;

/// <summary>Services on SQLite with real repositories: each test gets its own migrated database file.</summary>
public abstract class ServiceTestBase : IDisposable
{
    protected readonly TestDatabase _test = new();
    protected readonly SqliteDatabase _db;
    protected readonly CaseStore _cases;
    protected readonly ListStore _lists;
    protected readonly JobStore _jobs;
    protected readonly CaseService _caseService;
    protected readonly ListService _listService;
    protected static readonly DateTimeOffset T0 = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    protected static readonly string Gc11 = GoldenCases.GoldenCaseTests.Folder("GC-11");
    protected const string ProfileName = "名單資料_90000011.txt";

    protected ServiceTestBase()
    {
        _db = _test.Db;
        _cases = new CaseStore(_db);
        _lists = new ListStore(_db);
        _jobs = new JobStore(_db);
        _caseService = new CaseService(_cases, TimeProvider.System, _lists, ExportText);
        _listService = new ListService(_lists, _cases, _caseService, TxtCaseReader.ParseProfile, TxtCaseReader.ReadFiles, TimeProvider.System, ExportText);
    }

    public void Dispose()
    {
        _test.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static Dictionary<string, string> CaseFiles() =>
        Directory.GetFiles(Gc11, "*.txt").ToDictionary(p => Path.GetFileName(p), File.ReadAllText);

    protected BatchService Batch(BatchOptions? options = null) =>
        new(_jobs, _caseService, TxtCaseReader.ReadFiles, options ?? new BatchOptions(), TimeProvider.System, _listService);

    protected static string ExportText(string c) =>
        c.TrimStart().TrimStart('﻿').StartsWith('<') ? HtmlExport.ToExportText(c, HtmlExport.Kind.Profile) : c;

    protected static AnalysisRecord Failed(string caseId, int revision) =>
        new(caseId, revision, ExecutionStatus.Failed, ScreeningAssessment.NotEvaluated, new CaseAnalysis([], [], []), null, null, null, T0);
}
