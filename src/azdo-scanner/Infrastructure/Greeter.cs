using Microsoft.Extensions.Configuration;
using AzdoScanner.Core;

namespace AzdoScanner.Infrastructure
{
    public class Greeter : IGreeter
    {
        private readonly IConfiguration _configuration;
        public Greeter(IConfiguration configuration)
        {
            _configuration = configuration;
        }
        public string GetGreeting()
        {
            return _configuration["Greeting"] ?? "Hello, World!";
        }
    }
}
