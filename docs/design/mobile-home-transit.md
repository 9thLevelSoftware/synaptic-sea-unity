# Mobile-home transit verification

This follow-up builds on the delivered reclaimed-home player (`7fc2993`, delivery notes `fceaf4e`). The delivered player supports repairs, returning a recovered hull, welding a closed connection, two-way walking, Continue, and independent shuttle excursions. It does not establish natural earned home-engine installation or joined-home flight.

## Transit contract

A secured home extension is part of the home assembly when selecting its bridge. Selecting an independent moored shuttle still controls that shuttle. Ordinary travel must not detach a secured connection. The existing capability report counts each member's mass, cargo, power, hull condition and eligible engine once; a carried shuttle contributes payload but not thrust.

Home movement uses the game's discrete sea-coordinate travel model. It parks the assembly at a surveyed contact while retaining the local scene frame and member-relative poses used for walking. It does not simulate continuous propulsion, force integration or docking a large assembly through a shuttle berth. Boarding an encountered hull remains an independent-shuttle operation.

The home bridge requires completed onboarding and a prior expedition, an unmoored valid assembly, local navigation/propulsion controls and sufficient aggregate capacity. A bare home installation uses the existing `commission_home_propulsion` action: repair level 4, a qualifying welding tool, one thruster nozzle, one fuel line and one circuit board, with interruptible work. These parts are consumed by the existing work system; tests do not grant them.

The existing alternative fabrication definitions are `craft_thruster_nozzle` (known book recipe, tier-2 fabricator, skill 4, titanium ingot 2, ceramic plate 2, coolant fluid 1, 60 seconds) and `craft_fuel_line` (fabricator, skill 1, synth fiber 2, polymer pellet 2, adhesive paste 1, 20 seconds, producing two lines). Engineering salvage rolls can also supply both parts, with the nozzle rare. These are data-defined routes, not an accepted fresh gameplay acquisition sequence.

Moving home saves an optional versioned sea location. Older stationary-home saves keep their previous field contract. Malformed/nonfinite coordinates reject before live-state mutation. A shuttle excursion alongside a retained extension saves its actual local destination scene offset, avoiding overlap and preserving Continue placement. Pressure and power are still local to each vessel; no conduits are implemented.

## Evidence and remaining acceptance

The initial fresh flight journey failed the retained survival assertion while hauling bulk salvage. Actual health-loss telemetry identified combat, encumbrance, sanity and wounds; it did not justify changing survival rates. The retry uses the earned working wreck's real cargo hold to stow bulk items. Engineering salvage must complete its authoritative objective, not merely its presentation state.

The fresh retry failed after 325.26 seconds at the engineering salvage standing approach, before installation or transit. The objective lies at the sealed hatch at local `(84, 0.12, -4)`; its collider remains authoritative. The test helper opens ordinary authored doors but cannot treat a utility-locked hatch as an unlocked door. The utility definitions require a consumed lockpick/hack-chip flag. Current StreamingAssets data contains those definitions/effects but no matching loot or recipe entries. No bypass tool was granted and no lock was removed. This blocks the selected direct engineering-salvage route; it does not prove that every other surveyed engineering source or fabrication route is inaccessible.

The retry's actual health-loss ledger was combat 63.00, atmosphere 47.26, fire 21.53 and wounds 19.32, with no recorded encumbrance loss. Both finite field medkits and eight purified water were used. This is one observed route, not a balance prescription. The NUnit failure is the standing approach; these numbers must not be mislabeled as proof of death or an unavoidable economy lock.

Five focused Core regressions pass: insufficient-capacity denial/self-dock prevention, successful installed-home transit and scanner-range denial, secured-member bridge normalization with independent-shuttle control, location save/legacy/malformed-coordinate handling, and actual clamped health/inventory-debit observation. The successful transit check installs propulsion in an explicit fixture; it is not natural acquisition evidence. See `builds/logs/assembly-transit-final-focused.trx` and the failed natural case in `assembly-flight-natural-retry.xml`.

Final checks of the transit/telemetry source passed 791 Core tests and 1,069 Unity Edit tests, with 17 unchanged companion-library skips and zero failures (`assembly-transit-final-core.trx`, `assembly-transit-final-edit.xml`). The full mobile-home Play aggregate and a new Windows build are not accepted. The fresh journey test and cargo-handling diagnostics remain work in progress; keep the previously delivered executable for play.

Diagnostic observers report actual health lost after clamping and actual inventory removals. Inventory removals include equipment and cargo transfers, so they are not synonymous with consumption. Direct vitals changes have a generic source label. The observers are optional and do not change balance or saved state.

Natural acceptance remains pending until a fresh Title/New Run journey earns the materials, installs/activates propulsion, moves the assembled home, Continues at the destination and completes an independent shuttle departure/return without fixture grants. A passing mass fixture alone cannot establish this path. New Windows evidence must use a distinct build; the previously delivered player remains unchanged.
