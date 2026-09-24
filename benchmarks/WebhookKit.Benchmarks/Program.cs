using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

var config = ManualConfig.CreateMinimumViable()
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddExporter(MarkdownExporter.GitHub)
    .AddExporter(CsvExporter.Default)
    .AddExporter(JsonExporter.Full);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
