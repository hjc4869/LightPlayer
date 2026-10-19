using System;
using System.Collections.Generic;

namespace LightStudio.LightPlayer.Models;

public sealed record PageSearchEntry(
	object Item,
	string Text,
	IReadOnlyList<object>? Ancestors = null,
	Action? Prepare = null);