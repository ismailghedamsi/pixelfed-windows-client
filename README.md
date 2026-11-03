# Pixelfed Windows Client

A native Windows desktop client for Pixelfed, built with C#, WinUI 3, and the Windows App SDK.

## Phase 1 status

The app shell includes Feed, My profile, Bookmarks, Collections, and Settings navigation. On first launch, a dialog lets you select a Pixelfed server and sign in. Feed loads the latest 20 home timeline posts; My profile shows your posts and offers collection actions from each post’s three-dot menu. See [Phase 1 scope](docs/phase-1-scope.md) for product decisions and the six-month roadmap.

## Run locally

Install the .NET 10 SDK and Windows App Development CLI, then from this folder run:

```powershell
winapp run
```

The generated project uses the official WinUI NavigationView template. Microsoft’s [WinUI getting-started guide](https://learn.microsoft.com/en-us/windows/apps/get-started/start-here) lists current environment prerequisites.

## Product constraints to carry forward

- Pixelfed is federated: each account belongs to an instance, and the app must use that instance’s API.
- The instance check reads `/api/v2/instance` and `/api/v2.1/config`; the latter reports whether OAuth is enabled.
- OAuth app credentials are registered separately per instance. The client requests `read` for feeds and `write` for collection changes; Pixelfed’s write scope is broader than collections, and the consent page displays its permissions.
- OAuth access and client tokens are stored in Windows Credential Locker. The sign-in web view is restricted to HTTPS pages on the chosen server and the app’s OAuth callback.
- The Feed uses Pixelfed’s authenticated `/api/v1/timelines/home` endpoint and asks for Pixelfed-shaped status entities with `_pe=1`.
- My profile uses `/api/v1/accounts/verify_credentials` and `/api/v1/accounts/{id}/statuses`. Collection actions use `/api/v1.1/collections/self` and `/api/v1.1/collections/add`.
- Pixelfed has no authenticated collection-creation endpoint; the create action opens the instance’s `/i/collections/create` page in an in-app web view, then refreshes the collection list and adds the selected post.
- Pixelfed collections are server-side albums. The API can list collections and add eligible posts owned by the signed-in account.
- The documented API has no authenticated create-collection endpoint. The planned workaround is an in-app web view to `https://<instance>/i/collections/create`, followed by a collection refresh in the client.
- Pixelfed’s add endpoint currently accepts only the signed-in user’s public or unlisted photo, album, and video posts.
