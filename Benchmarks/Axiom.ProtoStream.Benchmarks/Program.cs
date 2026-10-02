using BenchmarkDotNet.Running;

// dotnet run -c Release --project Benchmarks/Axiom.ProtoStream.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(Axiom.ProtoStream.Benchmarks.FramedReadBenchmarks).Assembly).Run(args);
