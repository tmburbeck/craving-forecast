// MAUI's implicit usings bring in Microsoft.Maui.Controls.Trigger, which clashes with the
// engine's Trigger enum. An alias wins over namespace imports, so this settles it app-wide.
global using Trigger = CravingForecast.Core.Trigger;
