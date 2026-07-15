namespace Sinapsi.Governance.Accountability;

/// <summary>
/// A named, accountable owner for a line of defense — the answer to "who is answerable
/// when the pipeline makes a verdict". <see cref="Role"/> is the durable role name;
/// <see cref="Named"/> is the concrete human/component filling it; <see cref="Mechanism"/>
/// names HOW that owner reaches its judgment (used to prove two owners are independent —
/// a different mechanism, not a second pass of the same one).
/// </summary>
public sealed record AccountableOwner(LineOfDefense Line, string Role, string Named, string Mechanism);
