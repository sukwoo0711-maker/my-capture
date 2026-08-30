// WPF imaging (BitmapImage decode, encoders) is exercised by the image-store tests on
// dedicated STA threads. Running multiple such tests concurrently can destabilise the
// WPF imaging stack, so this assembly opts out of xUnit's cross-collection parallelism.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
