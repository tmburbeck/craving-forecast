# Craving Forecast

A satiety and craving pattern predictor. Instead of logging food, users rate their hunger or craving (1–10) and tag the context. The app learns their personal "danger zones" and sends a nudge 30 minutes before each one, matched to the trigger: a high-volume, high-protein snack or a quick distraction.

## How the prediction works

| Layer | When it leads | What it does |
|---|---|---|
| **1. Archetypes** | Day 1 | A 9-question, ~60 second quiz scores six rule-based archetypes (Afternoon Slumper, Revenge Bedtime Snacker, Post-Work Decompressor, Backloader, Boredom Grazer, Weekend Drifter). Each one adds risk windows based on the user's own wake time, bedtime and shift. |
| **2. Daily context** | Days 2–7 | Passive signals adjust each day: sleep (HealthKit or a one-tap morning question), steps, back-to-back meetings from the calendar, plus Friday-evening and Sunday-afternoon rules. Poor sleep raises risk across the whole day (higher ghrelin, lower leptin). |
| **3. Bayesian personalization** | Day 7 onward | Each hour blends baseline and personal data: `risk = (1 − w) · baseline + w · personal`, where `w = ceiling(day) × n / (n + 2)`. The ceiling is 0% on day 1, 50% on day 7 and 100% on day 14. The `n / (n + 2)` term is confidence weighting: an hour only moves to personal data once it has enough check-ins. |

**"Was this accurate?" loop.** After each window, the user confirms or corrects the prediction. Every rule covering that window has a trust score, modeled as Beta(3 + accurate, 1 + 1.5 × missed). Misses count more because they carry more information. A rule whose trust drops below 40% is phased out. Answers also become weighted check-ins (×3 for a confirmed or relocated craving, ×2 for a miss), so the personal layer learns faster than it would from routine check-ins alone.

## Projects

```
src/CravingForecast.Core     Pure engine: archetypes, context signals, blending, zones, nudges, feedback. No dependencies.
src/CravingForecast.Api      ASP.NET Core minimal API (JSON-file storage behind IUserStore).
src/CravingForecast.Mobile   .NET MAUI app (iOS + Android): quiz, check-ins, HealthKit, calendar, scheduled nudges.
tests/CravingForecast.Tests  xUnit tests for the engine and the signal classifiers.
```

## Run it

Requires the .NET 10 SDK (pinned in `global.json`).

```bash
dotnet test tests/CravingForecast.Tests
cd src/CravingForecast.Api && dotnet run           # http://localhost:5077
```

Then open `CravingForecast.Api.http` in Rider, Visual Studio or VS Code (REST Client extension) and work through the requests in order.

### Mobile

```bash
dotnet workload install maui
dotnet build src/CravingForecast.Mobile -f net10.0-android    # emulator calls the API at 10.0.2.2:5077
```

- **iOS** needs a Mac with Xcode, plus an Apple developer account for the HealthKit entitlement.
- **On a physical phone**, run the API with `dotnet run --urls http://0.0.0.0:5077` and change `ApiConfig.BaseUrl` to your computer's LAN IP.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/users` | Onboarding quiz → user id and day-1 forecast |
| GET | `/api/users/{id}/forecast?date=` | Hourly risk, zones, archetypes, factors, rule trust, next nudge |
| PUT | `/api/users/{id}/context/{date}` | Sleep / meetings / activity (only fields you send change) |
| POST | `/api/users/{id}/checkins` | 1–10 rating with optional context tags |
| GET | `/api/users/{id}/feedback/pending` | Today's finished zones not yet rated |
| POST | `/api/users/{id}/feedback` | `Accurate`, `Missed`, or `DifferentTime` + `actualHour` |
| GET | `/api/users/{id}/nudges?days=2` | Upcoming nudges for the phone to schedule |

## Design notes

- **Nudges are local notifications** scheduled on the device from `/nudges`, so they fire when the app is closed and no APNs or FCM server is needed. The app reschedules after each sync, check-in or feedback answer. iOS allows 64 pending notifications per app, which is plenty for 2 days.
- **Health data stays coarse.** The phone sends only `Poor / Ok / Good` and `Low / Normal / High`, never raw sleep or step counts. Calendar event details never leave the device; only a back-to-back yes/no is sent.
- **Health Connect (Android)** is stubbed. Android users get the morning sleep tap instead. See `HealthConnectSignals.cs` for wiring it up.
- **Storage** is one JSON file per user. Swap in EF Core with SQLite or Postgres by implementing `IUserStore`.

## Next steps

- [ ] EF Core persistence + auth (the API currently trusts the user id)
- [ ] Health Connect implementation
- [ ] Weekday vs weekend personal patterns (personal estimates are currently by hour of day only)
- [ ] Tune context factors per user the same way rule trust is tuned

Baseline rules describe general patterns and are not medical advice.
