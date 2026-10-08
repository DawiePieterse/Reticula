namespace Reticula.Infrastructure.Calc;

/// <summary>A PMTiles archive from the calc service, with what it holds.</summary>
public sealed record MapExtract(byte[] Data, int TileCount, int MaxZoom, string Source);
