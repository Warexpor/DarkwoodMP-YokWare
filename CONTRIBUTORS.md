# Contributors

## Co-authors

**Warexpor** and **Yokyy** co-author YokWare Branch.

| Co-author | Focus |
|-----------|--------|
| **Warexpor** | Path B Horde remaster load path; public repo; **Ironbark** protocol (IBP); co-op hardening and product direction |
| **Yokyy** | Original co-op house — structure, reliability hop, dedicated server path, SyncCheck, chat/HUD patterns; Path A lineage under `archive/yokyy-merge-0.9/` |

Path B vs Path A is a load-path choice, not a ranking of people.

## Lineage

- **DWMP Horde Remaster** — **current shippable sync base** (host-authoritative combat, entity stream, audio, campaign domains).
- **Ironbark (IBP)** — Warexpor’s typed packet wire (codec + dedicated-server tree); not the live Horde LAN peer protocol.
- Prior **YokWare 0.9 Path A** merge (Yokyy vessel + Ironbark integration + partial ports) is archived; not the load path.
- **LiteNetLib** — network transport (third-party).
- **BepInEx** / **MelonLoader** / **Harmony** — mod loaders and patching (third-party).

## Contributing

Pull requests welcome under **GPLv3**. Product version **0.9.x** Path B unless co-authors bump. Live wire is **Horde protocol 19** until an Ironbark bridge is intentionally reintroduced.
