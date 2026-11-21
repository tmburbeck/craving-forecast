#!/usr/bin/env bash
# Publishes the walk-through screenshots to the "screenshots" branch, then, for a pull request,
# posts them as a comment (updating the same comment on later pushes).
#   publish-screenshots.sh <folder of .png files>
# Needs GH_TOKEN, GITHUB_REPOSITORY, GITHUB_SHA, RUNNER_TEMP, and PR (the pull request number) for pull requests.
set -euo pipefail

src="$(cd "$1" && pwd)"
dest="main"; [ -n "${PR:-}" ] && dest="pr-$PR"
repo="$GITHUB_REPOSITORY"
url="https://x-access-token:${GH_TOKEN}@github.com/${repo}.git"
work="$RUNNER_TEMP/screenshots-branch"
shown="${SHOWN_SHA:-$GITHUB_SHA}"

git config --global user.name "github-actions[bot]"
git config --global user.email "41898282+github-actions[bot]@users.noreply.github.com"

if git ls-remote --exit-code --heads "$url" screenshots >/dev/null 2>&1; then
  git clone -q --depth 1 --branch screenshots "$url" "$work"
else
  git init -q -b screenshots "$work"
  git -C "$work" remote add origin "$url"
  printf 'Screenshots taken by CI. main/ is the latest main branch; pr-N/ belongs to pull request N.\n' > "$work/README.md"
fi

rm -rf "${work:?}/$dest"
mkdir -p "$work/$dest"
cp "$src"/*.png "$work/$dest/"
cd "$work"
git add -A
if git diff --cached --quiet; then
  echo "Screenshots unchanged."
else
  git commit -qm "Screenshots for $dest (${GITHUB_SHA::7})"
  for attempt in 1 2 3; do
    git push -q origin HEAD:screenshots && break
    [ "$attempt" = 3 ] && { echo "Couldn't push screenshots." >&2; exit 1; }
    git pull -q --rebase origin screenshots
  done
fi

[ -z "${PR:-}" ] && exit 0

# Pin image links to this commit so older comments keep showing the screens they were posted with.
sha="$(git rev-parse HEAD)"
img() { echo "<img src=\"https://github.com/$repo/blob/$sha/$dest/$1.png?raw=true\" width=\"$2\" alt=\"$3\">"; }
marker="<!-- salus-screenshots -->"
body="$marker
### Screens on this pull request

Taken by the web walk-through for ${shown::7}: sign-up, quiz, check-in and sign-out all passed.

| Phone | iPad | Desktop |
| --- | --- | --- |
| $(img today-phone 240 'Today on a phone') | $(img today-ipad 300 'Today on an iPad') | $(img today-desktop 420 'Today on a desktop') |
| $(img insights-phone 240 'Insights on a phone') | $(img insights-ipad 300 'Insights on an iPad') | $(img insights-desktop 420 'Insights on a desktop') |

<details><summary>Welcome, sign-up, quiz and check-in</summary>

$(img welcome-phone 200 'Welcome') $(img signup-phone 200 'Create account') $(img quiz-phone 200 'Quiz') $(img checkin-phone 200 'Check in')

</details>"

existing="$(gh api "repos/$repo/issues/$PR/comments" --paginate --jq ".[] | select(.body | startswith(\"$marker\")) | .id" | head -n1)"
if [ -n "$existing" ]; then
  gh api -X PATCH "repos/$repo/issues/comments/$existing" -f body="$body" > /dev/null
  echo "Updated the screenshots comment on #$PR."
else
  gh api "repos/$repo/issues/$PR/comments" -f body="$body" > /dev/null
  echo "Posted screenshots on #$PR."
fi
