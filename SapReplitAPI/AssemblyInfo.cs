using System.Runtime.CompilerServices;

// Grants SapReplitAPI.Tests access to `internal` members. Used sparingly — only
// where a genuine test seam is needed and the concrete dependency (SapService,
// COM-bound) cannot be mocked. See ProductCacheService.UpsertProductsAsync.
[assembly: InternalsVisibleTo("SapReplitAPI.Tests")]
