namespace SlothBox.Tests.Api;

public static class EndpointCases
{
    public const string FileId = "7f3c2a9e-1b4d-4c8e-9a2f-0d6b5e4c3a21";

    public static IEnumerable<object[]> All =>
    [
        ["GET", "/api/receipts?month=2026-10", ""],
        ["GET", "/api/receipts/export.csv?month=2026-10", ""],
        ["GET", "/api/categories", ""],
        ["POST", "/api/categories", """{"type":"receipt","name":"Example","description":"Synthetic","folder":"Example"}"""],
        ["PUT", "/api/categories/1", """{"name":"Example","description":"Synthetic","folder":"Example"}"""],
        ["DELETE", "/api/categories/1", ""],
        ["GET", "/api/folders", ""],
        ["POST", "/api/folders/check", """{"path":"D:\\slothbox-test"}"""],
        ["POST", "/api/folders", """{"path":"D:\\slothbox-test","label":"Synthetic","mode":"move","tidyExisting":false}"""],
        ["PATCH", "/api/folders/1", """{"enabled":false,"mode":"copy","label":"Synthetic"}"""],
        ["POST", "/api/folders/1/tidy-existing", ""],
        ["DELETE", "/api/folders/1", ""],
        ["POST", "/api/system/pick-folder", ""],
        ["GET", "/api/settings", ""],
        ["PUT", "/api/settings", """{"startAtLogin":false,"laziness":{"receiptSeconds":30,"screenshotSeconds":15,"documentSeconds":15,"otherSeconds":5},"ollamaUrl":"http://127.0.0.1:11436"}"""],
        ["GET", "/api/setup", ""],
        ["POST", "/api/setup/complete", """{"outputRoot":"D:\\slothbox-test-output","folders":[],"finishAnyway":true}"""],
        ["GET", "/api/phone", ""],
        ["POST", "/api/phone/rotate", ""],
        ["GET", "/api/ignored", ""],
        ["DELETE", "/api/ignored/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ""]
    ];
}
