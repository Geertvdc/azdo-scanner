using Spectre.Console;

namespace AzdoScanner.Core
{
    public static class BannerPrinter
    {
        public static void Print()
        {
            AnsiConsole.Write(new FigletText("ZURE").Color(Color.White));
            AnsiConsole.Write(new FigletText("AzDo Assessor").Color(Color.Grey));
        }
    }
}
