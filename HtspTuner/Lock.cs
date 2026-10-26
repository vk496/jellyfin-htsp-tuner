// .NET 9's dedicated lock type does not exist on net8.0 (the Jellyfin 10.10 build). Every use is a plain
// `lock` statement, which takes an object's monitor just the same, so there the name means object.
#if !NET9_0_OR_GREATER
global using Lock = System.Object;
#endif
