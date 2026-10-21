using Microsoft.Extensions.FileProviders;

namespace SlothBox.Server.Phone;

// The phone page is built by the UI into wwwroot/phone/index.html; it is absent until then.
public sealed class PhonePage(IFileProvider files)
{
    public IFileInfo File => files.GetFileInfo("phone/index.html");
}
