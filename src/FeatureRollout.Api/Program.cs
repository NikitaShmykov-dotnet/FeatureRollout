using FeatureRollout.Api;

var app = FlagApi.Build(args);
await app.Services.GetRequiredService<PublicationStore>().Migrate(app.Lifetime.ApplicationStopping);
await app.RunAsync();
