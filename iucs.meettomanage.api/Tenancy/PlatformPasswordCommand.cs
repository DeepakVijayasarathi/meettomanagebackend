using System.Text;
using iucs.meettomanage.application.Helper;

namespace iucs.meettomanage.api.Tenancy;

public static class PlatformPasswordCommand
{
    public static void Run()
    {
        Console.Error.Write("Platform password (at least 12 characters): ");
        var password = new StringBuilder();
        if (Console.IsInputRedirected) password.Append(Console.ReadLine());
        else
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
                else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
            }
            Console.Error.WriteLine();
        }
        if (password.Length < 12) throw new InvalidOperationException("Use a password of at least 12 characters.");
        Console.WriteLine(new BcryptPasswordHasher().Hash(password.ToString()));
    }
}
