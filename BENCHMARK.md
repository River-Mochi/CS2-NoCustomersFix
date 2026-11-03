# NCF benchmark mode (developer-only)

Build **Release PERF TEST**, not Debug:

```bash
dotnet build ncf-code/NoCustomersFix.csproj -c Release -p:NCFPerfTest=true -t:Rebuild
```

The PERF TEST DLL includes an **Options > No Customers Fix (Perf Test) > Benchmark > NCF balancing enabled** switch.

- **ON** (default): NCF balances Commercial, Leisure, Hotels and production, and logs simulation speed.
- **OFF**: The balancing systems skip new corrections; only the speed and NEC icon logger runs. A pending employee restoration from an ON-to-OFF switch is completed safely.

**Switching ON/OFF takes effect immediately, even with a city loaded.** No game restart needed. For a fair comparison, reload the **same original save** between trials; do not overwrite that save.

Suggested procedure: Set AdvancedSimulationSpeed to 8x, load the same 6 AM save and run past 9 AM. Repeat with the other mode using the same DLL, same playset, and a fresh load of that save. No manual speed tracking needed.

Log records:

- `[NCF-BENCHMARK]`: current mode at city load and live toggle changes
- `[NCF-SPEED]`: mode, city time, selected 8x target, actual smooth speed, frame, and `necIcons` every ~10 real seconds
- `[NCF-SPEED-HOUR]`: for each completed game hour, 8x-only sample count, mean, median, min and max, plus the NEC icon count at the first and last samples
- `[NCF-PERF-*]`: detailed workload timing when balancing is ON

The mode and speed monitor are excluded completely from ordinary public Release builds:

```bash
dotnet build ncf-code/NoCustomersFix.csproj -c Release -p:NCFPerfTest=false -t:Rebuild
```

**Benchmark OFF** is not an unmodded game. The `necIcons` field counts active No Customers notification icon entities using the same prefab-match method as City Watchdog, for both modes. A saved game can already contain shopping needs from an earlier NCF-ON session; disabling NCF prevents new corrections but does not retroactively erase saved shopping requests.
