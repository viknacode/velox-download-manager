using Velox.App.Services;

namespace Velox.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // O Chrome inicia este mesmo executável como "native messaging host",
        // passando a origem da extensão como argumento. Nesse modo não há UI:
        // só repassamos as mensagens para a instância do Velox via named pipe.
        if (NativeHost.IsHostInvocation(args))
            return NativeHost.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
