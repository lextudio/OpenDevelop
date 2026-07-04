using System.Collections.Generic;
using SampleApp.Models;

namespace SampleApp.Services
{
    public sealed class WidgetService
    {
        public IEnumerable<Widget> GetWidgets() => new List<Widget>();
    }
}
