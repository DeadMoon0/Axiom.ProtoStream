using BenchmarkDotNet.Running;

// dotnet run -c Release --project Benchmarks/ProtoStream.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(ProtoStream.Benchmarks.FramedReadBenchmarks).Assembly).Run(args);
