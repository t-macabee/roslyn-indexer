namespace Lurp.Workspace;

internal interface IContextTierBuilder
{
    string Name { get; }
    List<CapsuleItem> Build();
}
