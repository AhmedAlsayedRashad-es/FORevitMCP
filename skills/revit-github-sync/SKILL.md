---
name: revit-github-sync
description: Upload the FirstOption Revit command library to GitHub with the MCP tools github_status and github_push. Use when the user asks to share, back up, publish, or sync saved Revit commands, or after you save commands while GitHub auto-push is off.
---

# Sync the command library to GitHub

## Steps

1. Call `github_status`.
2. When `configured` is false, stop. Tell the user: "Open Revit > First Option > AI Bridge > GitHub Settings. Fill in the repository owner, name, branch, and a fine-grained token with Contents: Read and write. Click Test connection, then Save." Never ask for the token in the chat.
3. Before you push, check the saved code for secrets: tokens, passwords, personal paths, client names that must stay private. Tell the user when you find one.
4. Call `github_push` with a message that names the commands: `Add create_wall_grid; update place_door_family`.
5. Give the user the commit URL. The Revit panel also shows an "Uploaded to GitHub" notice.

## Errors

| Error | What to do |
|---|---|
| GitHub is not set | Step 2. |
| git push failed ... 401 / 403 / Authentication failed | The token is wrong, expired, or has no Contents write access. The user fixes it in GitHub Settings. |
| Repository not found | The user creates the repository on GitHub first (it can be empty). When the user agrees and `gh` is installed: `gh repo create <owner>/<name> --private`. |
| git is not installed | `winget install --id Git.Git -e` |

The MCP pulls with rebase and pushes again once when GitHub has newer commits. When that also fails, show the git error to the user.
