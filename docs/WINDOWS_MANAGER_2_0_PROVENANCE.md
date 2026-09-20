# Windows Manager 2.0 provenance and disposition

This record captures the donor and design gate used for the Windows lifecycle
reliability work. It is source provenance, not a claim of pixel parity or full
platform feature parity.

## Donor identity

- Repository: `PrometheusProphet/codexpro-safe`
- Branch: `main`
- Commit: `a4823b114f450e9ad62a2f4aa1bc891853a434b7`
- Remote ref when inventoried: `origin/main`
- Inventory date: 2026-09-20

The following SHA-256 values describe the material macOS Manager source, tests,
packaging, installation, and certification owners at that commit:

```text
d94e4a5319ee4d138fe0811e39051dd51c47ca27a798cc8ce7c2f52b3b04a7c2  docs/MACOS_MANAGER.md
82fcd970aa8cf549c6d95b22debe519d28377e71868336485f2cf4f0c8043536  tools/CodexProSafe.Manager.Mac/Package.swift
3b5bf51bb03ccb7c03da1217676d7bafcb6bd4f3a81e5d32401293e566435dc9  tools/CodexProSafe.Manager.Mac/Resources/Info.plist
52b542997519ea453d6da11de2cc2a969004af464308a015226a20d1bc4b210a  tools/CodexProSafe.Manager.Mac/Sources/CodexProSafeDiagnosticHelper/main.swift
dd66f5af34bd562912e6aa46805328923aaea766904c7909b1feac392dcc227f  tools/CodexProSafe.Manager.Mac/Sources/CodexProSafeLauncher/main.swift
25f08f93aa14079fa308d32ef2e8341067ba56ec37a265ddc6d775e1745eaac4  tools/CodexProSafe.Manager.Mac/Sources/CodexProSafeManager/CodexProSafeManagerApp.swift
427d02ccc25bc8c617a07337ded5e5aa40ca836d2ea922421e48f2c7a92f934e  tools/CodexProSafe.Manager.Mac/Sources/CodexProSafeTakeoverHarness/main.swift
33e64397286d563681dc0effcc52c83fbea41459db8836ae8683a35e51fe8282  tools/CodexProSafe.Manager.Mac/Sources/ManagerCore/ExternalConnector.swift
29031766b79eb59e256366c20143ea93bb2e99c74b94d7162375be42eaa67bae  tools/CodexProSafe.Manager.Mac/Sources/ManagerCore/ManagerCore.swift
090feaa09a35669b8b798141ffc1e274c07542912b580ec1610ec861752c0ec7  tools/CodexProSafe.Manager.Mac/Sources/ProcessInspectionC/ProcessInspectionC.c
1c4c3a55dbfeb1486aec68456646e9e616299473fd04446ef7e5f0b08b1ccb80  tools/CodexProSafe.Manager.Mac/Sources/ProcessInspectionC/include/ProcessInspectionC.h
adef0efd1c5da3849bcd9570087c1122b98c605768be56fb22a856d682b31cb2  tools/CodexProSafe.Manager.Mac/Tests/ManagerCoreTests/ManagerCoreTests.swift
041abe59f47e38c91a7a0e2a927927adbd41c4b504e622fc1186ce906fe9ff9e  scripts/install-macos-manager-development.sh
5dba1f1d9cebd039143f86f9341f4660d324843ca9d56271b165814aef0795d4  scripts/macos-manager-accessibility-contract.test.mjs
808717dfeffec43571ded22f61ad4e1f4f8a50bf1016f22c9ae649965a5a4391  scripts/macos-manager-autostart-smoke.mjs
5c9003e2d806f500024b64760669117041d8d41a4c3614b18fe3f6c4bc36a9ee  scripts/macos-manager-install-lifecycle-smoke.sh
8e230e8be4b8fb286e1205e07bed04520c0594c6245a8fb0e11f53a2ab738be1  scripts/macos-manager-live-certification.mjs
5e8585004352a556451f85b95c426dd01c1b3727d74688b56210608872b24aab  scripts/macos-manager-login-item-smoke.sh
ad035fb29efb15c3e9503cd4ba47dc3f260c4228d4379b9a0e7b47565c877fb8  scripts/macos-manager-network-cycle.mjs
23f95a2d2dae3ff373c543a76178ce01f9e93196e104eeed98f6560ef2ae53e6  scripts/macos-manager-release-preflight.sh
fe8cde84cc53b2e74ddedc16160e16f7404a83e9baf7fd621451e791fd6e3a3f  scripts/macos-manager-release-readiness.sh
de085aa84385935ebe461e1bd702aea562d769a945cc42381d0941fa0405696b  scripts/macos-manager-secure-tunnel-smoke.mjs
3d74dfe918a408ec9623c325f0320570904703c28ff2bf96b3b5e634ed4b592b  scripts/macos-manager-smoke.mjs
6f8fea665643f233a51a3bf7321b119f0e0c2514a3e20d60c58f04ce55dcf543  scripts/macos-manager-takeover-smoke.mjs
9c68f902b37e45cfd53b3a25cd104842bbcb21833fb33364b9320d63c27295f8  scripts/package-macos-manager.sh
0e38e2aade68f013f16aaac7323fdd8ba83434d7e3190a6edfb709e9baed7e0f  scripts/verify-macos-manager-bundle.sh
cd3b113ef5bbd5fa965f5465019c9feade252fc5dcd61186029087818284ac9d  scripts/verify-macos-manager-installed.sh
```

## Capability and test-family disposition

| Family | Disposition | Windows result |
| --- | --- | --- |
| Exact takeover, Safe profiles, helper trust, authenticated readiness, redaction | Retain | Existing Windows owners remain authoritative and fail closed. |
| Deterministic stop/restart and race handling | Deepen | Stops bind PID plus creation time, accept verified already-exited races, reject PID reuse, prove captured-descendant and endpoint shutdown, and emit fixed categories. |
| Update, install, rollback, and removal proof | Deepen | Source/build proof is included here; live install/update/rollback remains behind the explicit operational approval gate. |
| Multi-workspace connector behavior and command jobs | Consolidate | Kept in shared package owners and shared npm tests, not copied into WinForms. |
| macOS process groups, Keychain, LaunchAgent/login item, app bundle signing, notarization | Exclude | Platform-specific and not valid Windows implementation provenance. |
| Windows DPAPI, ACL, startup task, private proof pipe, and UI privacy | Retain | Windows-specific security boundaries remain unchanged. |
| Arbitrary process control, raw logs/settings, generic desktop control | Exclude | Outside the Manager product identity. |

## Design decision and proof boundary

The Windows implementation improves the existing verified supervisor rather
than replacing it with a new Job-object lifecycle core. The current diagnostic
launch proof already uses a private Job object for its separate ownership
contract. Rebuilding every connector and tunnel launch around another Job layer
would enlarge the native launch surface and introduce nested-Job compatibility
risk without resolving the reported ambiguity by itself.

The smaller design keeps exact command/profile/root verification and adds a
testable postcondition classifier around the existing Windows tree stop. The
generated Manager self-test covers owned/external matching, mismatch refusal,
already-exited and repeated stops, timeout recovery, access denial, PID reuse,
partial tree shutdown, endpoint-still-live, and sanitized error categories.
Repository-pinned package, connector, privacy, helper, maintenance, and
documentation proof remains required separately.

No source task proves live installation, controlled service interruption,
authenticated tunnel readiness, or real ChatGPT plugin callability. Those
operations require explicit approval and a recoverable installed-package and
settings rollback.
