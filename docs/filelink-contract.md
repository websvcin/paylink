# What PayLink needs from FileLink

FileLink is the planned file service in the LoginLink / CrudApi / PayLink family. Until it ships, PayLink keeps files in its data folder. This page is the small contract PayLink's FileLink backend calls, so FileLink can implement it and PayLink can switch over without changes (Admin → Settings → File storage).

Every call carries `Authorization: Bearer {key}`, the key entered in PayLink. Calls are server to server: no FileLink key ever reaches a browser.

## Health

```
GET /api/v1/health
200                        the key is valid
401                        the key isn't
```

PayLink calls this before saving the settings.

## Store a file

```
PUT /api/v1/files/{path}
Content-Type: image/png
<bytes>

201 { "id": "f_8Kq2…" }
```

`{path}` is PayLink's own name for the file, `{tenant id}/{yyyy-MM}/{random}.{ext}`, so FileLink can keep each PayLink tenant's files apart. Files are at most 5 MB, and PayLink has already checked they're PNG, JPEG or WebP. PayLink stores the returned `id` as `filelink:{id}`.

## Read a file

```
GET /api/v1/files/{id}
200 <bytes>, with Content-Type
404                        no such file
```

PayLink streams it to a signed-in console member, and records the view in the tenant's audit log.

## Later

The blueprint's fuller FileLink (per-tenant workspaces, one-time upload tickets so the payer's browser uploads directly, short-lived signed links, storage connectors such as S3 or R2) can keep these three calls for PayLink, or PayLink adds the new ones when FileLink publishes them.
