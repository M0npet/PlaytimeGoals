# Security

PlaytimeGoals does not require credentials to be stored in its source
repository.

Never submit or commit:

- ASF bot configuration files
- Steam usernames or passwords
- Steam login keys
- SteamID/account-specific dumps
- Steam Family View PINs
- ASF IPC passwords
- cryptkey files
- TLS private keys
- authentication cookies or tokens
- ASF databases
- runtime logs
- PlaytimeGoals state databases
- PlaytimeGoals parental recovery journals

If a credential is ever accidentally committed, removing the file in a
later commit is not sufficient. Revoke or rotate the affected credential
and rewrite the Git history before publishing the repository.
