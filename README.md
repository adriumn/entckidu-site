# entckidu.net

Adi's landing page: music, drawings, laziness.
Hosted on Azure Static Web Apps (Free plan); every commit to `main` deploys automatically.

## Layout

```
site/                     the web page (what visitors download)
  index.html
  staticwebapp.config.json  tells Azure the API is .NET 8 (isolated)
  images/records/         record photos, 480×480 JPEG, named lp-band-album.jpg
  images/drawings/        the drawings strip
api/                      the Azure Function behind /api/album (C#, .NET 8)
.github/workflows/        the deploy pipeline (app in site/, API in api/)
```

## The tagline under the name

One small line of text under eNTckidu: `var TAGLINE = "";` in `site/index.html`.
Put the text between the quotes; leave it empty to hide the line.

## Album details

Clicking a record opens it big; the page then calls `/api/album`, and the
Function asks [MusicBrainz](https://musicbrainz.org) for year, label, members,
genres and tracks.

- `GET /api/album?artist=Jane&album=Lady` searches by name.
- `GET /api/album?mbid=<release id>` uses one exact MusicBrainz release.
  Add the ID as a third value in the `records` list when the search picks the
  wrong album, or when the entry has no album name (e.g. "Omega"):
  `["lp-omega.jpg", "Omega", "<release id>"]`.
  The release ID is the last part of a URL like `musicbrainz.org/release/<id>`.

MusicBrainz is free; it asks for at most ~1 request per second and a
User-Agent that names the app. The Function keeps to both and remembers
answers while it stays warm.

## Run locally (optional)

Needs the .NET 8 SDK, Azure Functions Core Tools and the SWA CLI
(`npm install -g @azure/static-web-apps-cli`):

```
swa start site --api-location api
```

Then open http://localhost:4280.
