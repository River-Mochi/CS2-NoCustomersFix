# NCF benchmark mode (developer-only)

Build **Release PERF TEST**, not Debug:

```bash
dotnet build ncf-code/NoCustomersFix.csproj -c Release -p:NCFPerfTest=true -t:Rebuild
```

The PERF TEST DLL includes an **Options > No Customers Fix (Perf Test) > Benchmark > NCF balancing enabled** switch.

- **ON** (default): NCF balances Commercial, Leisure, Hotels and production, and logs simulation speed.
- **OFF**: The balancing systems are present but immediately skip their updates, making no NCF corrections; only the speed logger does active work.

**Switching ON/OFF takes effect immediately, even with a city loaded.** No game restart needed. For a fair comparison, reload the **same original save** between trials; do not overwrite that save.

Suggested procedure: Set AdvancedSimulationSpeed to 8x, load the same 6 AM save and run past 9 AM. Repeat with the other mode using the same DLL, same playset, and a fresh load of that save. No manual speed tracking needed.

Log records:

- `[NCF-BENCHMARK]`: current mode at city load and live toggle changes
- `[NCF-SPEED]`: mode, city time, selected 8x target, actual smooth speed, frame every ~10 real seconds
- `[NCF-SPEED-HOUR]`: for each completed game hour, 8x-only sample count, mean, median, min and max
- `[NCF-PERF-*]`: detailed workload timing when balancing is ON

The mode and speed monitor are excluded completely from ordinary public Release builds:

```bash
dotnet build ncf-code/NoCustomersFix.csproj -c Release -p:NCFPerfTest=false -t:Rebuild
```

**Benchmark OFF** is a passive-logger baseline, not an unmodded game. Since the logger only samples occasionally, it is suitable for comparing the ON/OFF difference without eyeballing a rapidly fluctuating speed display.
