using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Lurp.Storage;

namespace Lurp.Adapters;

public interface IFrameworkAdapter
{
    string Name { get; }
    string Version { get; }
    List<EdgeRecord> Extract(Compilation compilation, string snapshotId);
}
