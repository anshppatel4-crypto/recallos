# RecallOS privacy tests

RecallOS is closed source, so you can't read how it works. You can read how its promises are checked.

These are the tests behind the privacy claims on [recallosai.com](https://www.recallosai.com/security), copied from the private repository at each release. They run on every build there, and a release can't be packaged if one fails. They don't compile on their own, because the app's code isn't here.

| The promise | Where it's checked |
|---|---|
| The history is unreadable without its key, on disk and after migrating from an older version | `EncryptionTests.cs` |
| A failed migration never loses the history | `EncryptionTests.cs` |
| A forgotten moment is really gone from the raw files, not just hidden | `ForgetForensicsTests.cs` |
| An excluded app or window never reaches the disk | `CapturePipelineTests.cs` |
| Private windows, password managers and banking sites are left out by default | `V3FeatureTests.cs`, `SensitiveWindowTests` |
| Card numbers, keys and tokens are removed from the picture and the search index | `RedactionTests.cs`, `RedactionPipelineTests.cs` |
| Age and size limits really delete old history | `RetentionTests.cs` |
| A model that isn't on your PC stays off until you agree to that exact address | `V3FeatureTests.cs`, `ServerModelTests` |
| The local API needs a token and refuses other websites | `V3FeatureTests.cs`, `LocalApiTests` |
| A vault file can't be opened without its code, and an altered one is refused whole | `V3FeatureTests.cs`, `VaultFileTests` |
| An organisation's policy can't be switched off from inside the app | `V3FeatureTests.cs`, `EnterprisePolicyTests` |

Any card numbers, keys and tokens in these files are made up, for testing.

These tests show what's checked, but they aren't an independent audit, and RecallOS hasn't had one yet. See [what is and isn't done](https://www.recallosai.com/security).

Found a problem? Email anshpatel@recallosai.com.

Copyright © 2026 RecallOS. All rights reserved. Published to read and review. No license is granted to reuse them.