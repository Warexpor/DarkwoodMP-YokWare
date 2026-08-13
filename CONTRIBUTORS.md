# Contributors

## Co-authors

**Warexpor** and **Yokyy** co-author YokWare Branch.

| Co-author | Focus |
|-----------|--------|
| **Warexpor** | Path B Horde remaster load path; public repo; **Ironbark** protocol (IBP); co-op hardening and product direction |
| **Yokyy** | Original co-op house — structure, reliability hop, dedicated server path, SyncCheck, chat/HUD patterns; Path A lineage (removed from repo) |

Path B vs Path A is a load-path choice, not a ranking of people.

## Lineage

- **DWMP Horde Remaster** — **current shippable sync base** (host-authoritative combat, entity stream, audio, campaign domains).
- **Ironbark (IBP)** — Warexpor’s typed packet wire (codec + dedicated-server); removed from repo; not the live Horde LAN peer protocol.
- Prior **YokWare 0.9 Path A** merge (Yokyy vessel + Ironbark integration + partial ports) was the pre–Path B line; not the load path.
- **LiteNetLib** — network transport (third-party).
- **BepInEx** / **MelonLoader** / **Harmony** — mod loaders and patching (third-party).

## Contributing

Pull requests welcome under **GPLv3**. Product version **0.7.x** Path B (current **0.7.79**; earlier **0.9.x** labels were too ambitious). Live wire is **Horde protocol 24**.
