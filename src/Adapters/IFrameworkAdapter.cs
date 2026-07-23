using Microsoft.CodeAnalysis;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Adapters;

public interface IFrameworkAdapter
{
    string Name { get; }
    string Version { get; }
    List<EdgeRecord> Extract(Compilation compilation, string snapshotId, EdgeLocationResolver locationResolver);
}
