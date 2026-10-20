namespace SlothBox.Tests.Api;

public static class EndpointCases
{
    public const string FileId = "7f3c2a9e-1b4d-4c8e-9a2f-0d6b5e4c3a21";

    public static IEnumerable<object[]> All =>
    [
        ["GET", "/api/status", ""],
        ["POST", "/api/pause", ""],
        ["POST", "/api/resume", ""],
        ["GET", "/api/activity?limit=50", ""],
        ["GET", "/api/stats/laziness?range=week", ""],
        ["GET", $"/api/files/{FileId}", ""],
        ["GET", $"/api/files/{FileId}/thumb", ""],
        ["GET", $"/api/files/{FileId}/preview", ""],
        ["POST", $"/api/files/{FileId}/open", ""],
        ["POST", $"/api/files/{FileId}/reveal", ""],
        ["POST", $"/api/files/{FileId}/undo", ""],
        ["POST", $"/api/files/{FileId}/refile", """{"type":"receipt","categoryId":1,"title":"Example","receipt":{"vendor":"Example","date":"2026-10-20","amount":10,"currency":"INR","paymentMethod":"cash"}}"""],
        ["POST", "/api/undo/last", ""],
        ["POST", "/api/undo/since", """{"since":"2026-10-20T00:00:00Z"}"""],
        ["GET", "/api/review", ""],
        ["POST", $"/api/review/{FileId}/skip", ""],
        ["GET", "/api/search?q=example&type=document&from=2026-10-01&to=2026-10-20", ""],
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
        ["DELETE", "/api/ignored/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ""],
        ["POST", "/api/search-data/rebuild", ""]
    ];
}
