namespace SlothBox.Server;

public partial class Program
{
    public static void Main(string[] args)
    {
        var app = ServerHost.Build(args);
        app.Run();
    }
}
