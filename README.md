# 🛒 Not Enough Customers Fix (NCF)

A Cities: Skylines II mod to reduce excessive **Not Enough Customers (NEC)** warnings in commercial buildings.

## Why this mod?

Shops can keep producing Service faster than customers can use it, especially when many citizens are at work or school.

Vanilla starts slowing production at 80% full Service, but shops can still reach the critical 90% NEC threshold.

## Features

- **Preventive balancing:** Starts reducing production at 80% unused Service and applies strong relief at 84%.
- **Adaptive production:** Slows production when shops are too full.
- **Shopping demand:** Uses existing households, not fake shoppers; caps added shopping needs at 24 per active update and targets 87% unused Service.
- **Leisure:** Helps Meals, Entertainment, and Recreation.
- **Hotels:** Best-effort balancing using existing tourists.
- **Performance:** Burst-optimized processing and reduced scanning.
- No Harmony patches or Options settings required.

## Test Results

- **130k city:** 165 NEC → 4 after ~30 minutes.
- **90k city (6:10 AM test save, already partly corrected):** ~50 NEC → 7 in the 0.6.4 test. An earlier save had ~335 NEC before NCF reduced them.

Allow 15–30 minutes for heavily affected cities to rebalance.

## Compatibility

- Cities: Skylines II 1.6.x
- Safe to remove; vanilla balancing resumes.
- Back up your saves when testing new mods.

## Credits

- **RiverMochi:** Author
- **Sunny Scunny:** Testing

## License

GPL-3.0-or-later with the Cities: Skylines II Linking Exception.
See [LICENSE](LICENSE) and [LICENSE-EXCEPTION](LICENSE-EXCEPTION).

## Feedback

Issues and suggestions are welcome on [GitHub](https://github.com/River-Mochi/CS2-NoCustomersFix).
