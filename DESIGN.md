# Design notes

Why this tool is shaped the way it is, what it deliberately does not do, and where it should go.

Everything below came out of building it against a working Synology NAS with 17 containers and then
proving it by emptying that host and restoring it. The findings are from that audit rather than from
a survey, which is why they are specific.

---

## 1. The problem

A container is three separate things, and most backup strategies capture only one:

| Part | Where it lives | Lost how |
|---|---|---|
| Run configuration | Docker's own metadata | Never on the filesystem, so a file backup never sees it |
| Named volume data | `<docker root>/volumes/<name>/_data` | Outside every Synology shared folder, so Hyper Backup never sees it |
| Bind mount data | ordinary host paths | Usually covered, but copied live and therefore often corrupt |
| Images | the image store | Assumed re-pullable, which is false for anything built locally |

A NAS backup that looks complete can be missing every database in Container Manager, because named
volumes live under the Docker root — which is not a shared folder.

---

## 2. What the audit found

Fourteen distinct problems on one ordinary host. Nothing here is unusual; it is what accumulates on
any machine that has been useful for a few years.

| # | Finding | Scale | Cost to a backup |
|---|---|---|---|
| 1 | Containers with no compose file | **10 of 17** | The definition has to be reverse-engineered from `docker inspect`, losing `mem_limit`, `sysctls`, `ulimits`, `logging`, `depends_on` and `security_opt` |
| 2 | Compose file stored inside another tool's volume | 1 project | Portainer keeps stacks at `/data/compose/<id>`, a path that exists only inside the Portainer container. Cannot be archived; Container Manager cannot adopt it |
| 3 | Unrelated data-root conventions | **4 of them** | No rule to infer. Every path has to be discovered and checked by hand |
| 4 | Bind mounts pointing at deleted folders | 6 paths | The container "restores" into an empty shell, silently, unless the tool reports it |
| 5 | Anonymous volumes | 1 | A 64-hex name carries no clue what it holds or whether losing it matters |
| 6 | Secrets inline in environment variables | a tunnel token, 2 database passwords | Every backup contains plaintext credentials, in both the inspect JSON and the generated compose |
| 7 | Floating image tags | **7 of 8 images** | A restore later fetches *different software*. A database directory written by an older major version refuses to start |
| 8 | One image referenced two ways | 1 pair | 565 MB stored twice, until deduplication moved to image IDs |
| 9 | Orphans nobody can identify | 1, exited 21 months | Backed up anyway, because nothing says whether it matters |
| 10 | Half-finished migration left in place | 2 containers, same app | Two config trees, no way to tell which is live |
| 11 | Shared user data bind-mounted into an app | 1 media share | Container data or personal library? The tool cannot tell, and the answer changes the backup |
| 12 | Documentation | **zero files** | Restoring correctly needs knowledge that exists only in someone's memory |
| 13 | Project registry separate from Docker | all projects | Containers run fine while DSM's Project view sits empty. Re-registering needs an undocumented API |
| 14 | Regenerable data mixed with precious data | throughout | Backups carry weight that never needed saving |

---

## 3. Four root causes

Fourteen symptoms, four causes. Each is a decision made at creation time that nobody could revisit,
because nothing recorded it.

**A. Intent is never written down.** Docker records *what* is mounted. It has no place to record
*why*, or whether losing it would matter. So a backup tool has to guess: is this a database that
needs stopping first, a cache worth skipping, or a media library that belongs to someone else? Every
guess is a chance to be wrong about somebody's data.

**B. The definition and the data live apart.** A stack is a compose file in one place, data in
another, documentation nowhere. There is no single thing you can copy, move, or hand to someone else.

**C. Absolute paths bind a stack to one machine.** Every absolute path in a compose file is a promise
about one specific host. Move to a new NAS, a different volume, or another user's home directory and
the promise breaks.

**D. Creation is a conversation, not an artifact.** Clicking through a wizard produces a running
container and no file. The configuration exists only inside the daemon, so there is nothing on disk
to back up, review, diff or version. Ten of the seventeen containers were made this way.

---

## 4. Decisions this tool already makes

### Transport: exec channels, never SFTP

DSM ships with the SFTP subsystem disabled, so SCP and SFTP both fail outright. All transfer streams
through a plain exec channel instead — `tar czf -` outbound, `tar xzf -` inbound, piped straight to
and from a local file. Nothing buffers in memory, so archive size is bounded by disk rather than RAM.

### Every command sets its own PATH

DSM's sudoers `secure_path` omits `/usr/local/bin`, which is where `docker` lives. This is why
`sudo visudo` reports "command not found" on a Synology and would do the same for `docker`. Rather
than requiring a sudoers change on the host, every command carries its own `PATH` into the remote
shell.

### The sudo password goes to stdin

Written to the command's **stdin** (`sudo -S -p ''`), so it never appears in `ps`, in shell history,
or in this app's own logs. For uploads, sudo consumes one line and then execs `tar`, which goes on
reading the same descriptor — so the archive simply follows the password down the same pipe.

The consequence is the design goal: **no `NOPASSWD` rule, no SSH key, nothing installed, nothing
changed on the host.** It works with the password you already have.

### Images are keyed by content ID, not by name

`postgres` and `postgres:latest` are the same image. Keying deduplication on the reference text
stores it twice — this cost 565 MB in a real backup before it was fixed. The same normalisation
applies when the restore pre-flight decides whether an image needs downloading, so one image named
two ways is not reported as missing.

### Image export defaults to everything

Re-pulling looks like the thrifty choice until you need it. Most images are tagged `:latest`, which
is a moving pointer rather than a version, so restoring a year later fetches different software.
Saving the image pins what actually ran and removes any dependency on the host having internet at
restore time.

### Containers stop before their data is copied

A tar of a live database is very often a corrupt database, and you find out at restore time. Stopped
containers are restarted afterwards — including when the run fails or is cancelled.

### Run state is reproduced, not assumed

Read fresh from `docker inspect` at backup time. A container that was running comes back with
`compose up -d`; one that was stopped is built with `compose create` and left alone. `create` rather
than "start then stop", so a container that was deliberately down never gets a moment of runtime in
which to run migrations.

### The original compose file wins over the generated one

A generated compose file is a reconstruction from inspect data and loses whatever inspect does not
expose. When the project's own file is still on the host it is used instead, which also keeps the
container inside the project Container Manager knows about. Networks are pre-created only on the
generated path — doing it on the original path makes compose reject the network for carrying the
wrong `com.docker.compose.network` label.

### Restore is two-phase

All data first, then all containers. A container whose data failed is not recreated, so a broken
restore does not leave a running service pointed at half-written state.

### Synology's project registry is restored too

Container Manager's Project tab is DSM's own bookkeeping, not a view over Docker, so a restore that
gets every container right still leaves that list empty. The tool calls `SYNO.Docker.Project` via
`synowebapi` to put the entries back. Two things make it work, and neither is documented anywhere:

- `synowebapi` prints diagnostics *before* its response, and those lines contain braces of their own
  (`param={…}`). Taking the first `{` in the output parses the echoed parameters instead of the
  result, which makes every call look like a failure even when it succeeded.
- A newly created project sits at `status: CREATED`, showing grey while its containers run. The
  API's `build` method fixes that, but DSM refuses a build while still settling the previous one
  (error 2104), and projects are registered back to back — so a refusal is retried.

### Host folders follow the containers they belong to

On the Restore tab, a project folder is matched to the containers whose compose directory it is, or
that bind-mount something inside it. Untick a container and its folder stops being written. Without
this the two lists are independent, and a partial restore overwrites a live project directory with a
stale copy — which happened once during development, to a folder holding uncommitted work.

### Failures are separated from expected skips

A container can legitimately reference a path that has been moved away, or mount host plumbing that
was never data. `docker.sock`, `/etc/localtime`, `/proc`, `/sys` and `/dev` are skipped by design —
restoring `/etc/localtime` would rewrite the host's own timezone. Reports keep **not archived
(deliberately)** apart from **failed**, so a real problem is not buried among expected noise.

### Everything is checked before anything is written

One round trip classifies every source, measures it, and compares the estimate against free space at
the destination. Deliberately not a mid-run dialog: nobody is sitting in front of the machine forty
minutes into a large transfer.

---

## 5. How this compares to existing tools

Two questions decide whether a container manager helps here: **where does the definition physically
live**, and **does its backup include your data**.

| Tool | Definition lives | Backs up your data | Restores a host |
|---|---|---|---|
| **Portainer** | Its own database, inside its own volume | **No** — its backup covers Portainer's configuration: environments, users, teams, registries, access rules. Explicitly not containers, stacks, volumes or workload data | No |
| **Synology Container Manager** | Project folder, plus a separate DSM registry | No | No |
| **Dockge** | `/opt/stacks/<stack>/compose.yaml` — plain files on the host, git-friendly | No | No |
| **Komodo** | Its own store, syncable from git | Partly — rebuilds definitions from git | Partly |
| **PaaS-style** (Coolify, CapRover, Dokku) | Their own store, by design | Varies; each manages data for apps it deployed and nothing else on the host | Varies |
| **offen/docker-volume-backup** | n/a — a companion container | **Yes**, named volumes, with `stop-during-backup` labels | No — volumes only, no definitions, networks or run state |
| **Nautical Backup** | n/a | **Yes**, volumes | No |
| **Restic / Borg / Kopia / Hyper Backup** | n/a | **Yes**, whatever paths you point at | No — entirely Docker-unaware, and will copy a database mid-write |
| **This tool** | On disk, per stack | **Yes** — volumes, binds, images, networks with their addressing, run state | Yes, verified against a deliberately emptied host |

### What is borrowed, not invented

Two ideas below are not original, and pretending otherwise would be the fastest way to build
something nobody trusts:

- **One directory per stack is Dockge's whole thesis.** It stores stacks as plain compose files on
  the host precisely so that files, not a database, are the truth. The layout in §6 extends that idea
  to cover data, secrets and documentation as well as the definition.
- **Labels declaring backup intent already exist.** `offen/docker-volume-backup` has long used
  `docker-volume-backup.stop-during-backup=true` for exactly the job a `backup.policy: cold` label
  would do, plus a no-restart variant. The right response is to **read theirs as authoritative too**,
  so a host already labelled for offen gets correct cold backups on first run with nothing to
  relabel. A vocabulary that demands migration before it helps does not get adopted.

### What appears to be genuinely missing

Set the borrowed parts aside and a real gap remains. Nothing in the field seems to:

- **Audit structural debt** — tell you that ten containers have no compose file, that one definition
  lives inside another container, that six mounts point at nothing, that a password is in plaintext,
  that seven of eight images float on `:latest`.
- **Migrate a stack out of a manager's database onto disk.** The existence of "escaping Portainer"
  guides is the evidence: it is a manual, error-prone afternoon.
- **Restore the definition rather than the bytes.** Volume-backup tools restore data into a volume.
  They cannot recreate the container, its fixed network address, its run state, or its project.
- **Restore the platform's own bookkeeping**, which lives outside Docker entirely.
- **Generate the documentation nobody writes**, from state that is already there for the asking.

### Positioning

This is not a better Portainer. Portainer is a management console for many environments and many
users, and competing with it would be foolish. This sits underneath one: **get a host into a shape
where a backup can be trusted, then prove it by restoring.** Which also means staying friendly to
what people already run — read Dockge's directories, read offen's labels, and treat a
Portainer-managed stack as a recognised, explained situation rather than an error.

---

## 6. Where this should go

The audit's real lesson is that backup is the *last* problem. Everything hard about it traces back to
decisions made when the container was created. So the more valuable tool assists at creation time.

### The layout

The whole idea in one sentence: **the folder a thing sits in declares whether it gets backed up**, so
nothing has to be inferred.

```
/volume1/docker/                      # one Docker root. On a Synology it must be a real
├── STACKS.md                         #   shared folder, or Container Manager cannot adopt it
├── _shared/                          # only things genuinely shared between stacks
│   └── media/                        #   big libraries — a file backup's job, not this tool's
│
└── myapp/                            # one stack = one directory = one backup unit
    ├── compose.yaml                  # the definition. relative paths only
    ├── .env                          # non-secret settings — safe to commit
    ├── .env.secret                   # secrets, mode 0600, never committed
    ├── README.md                      # what it is, its ports, how to bring it back
    │
    ├── config/                       #  ALWAYS  small, precious, hand- or app-edited
    ├── data/                         #  ALWAYS  the app's own state — stop container first
    ├── cache/                        #  NEVER   regenerable
    └── logs/                         #  NEVER   regenerable
```

Four subfolder names, two policies, no ambiguity. A tool reading this host does not need to ask what
`./cache` is, and neither does a person.

### The rules

1. **One directory per stack.** It is the unit of backup, move and restore. If you cannot copy the
   folder and have the application, the layout is wrong.
2. **Relative paths only in compose.** `./data:/var/lib/postgresql/data`. This is what makes a
   restore onto a different host or volume work at all.
3. **The subfolder name is the backup policy.** `config/` and `data/` kept; `cache/`, `logs/` and
   `tmp/` dropped. Nothing else to configure.
4. **Secrets in `.env.secret`, never inline.** Referenced through `env_file`, mode 0600, excluded
   from git, encrypted by the backup rather than sitting in plaintext inside an archive.
5. **Pin the digest, keep the tag.** `image: postgres:16.4@sha256:9f3a…` — the tag stays readable,
   the digest makes the restore deterministic.
6. **Every stack has a README.** Generated from live state first, edited by a person after. One that
   is 80% right beats the nothing that exists today.
7. **Anything reaching outside the stack is declared**, so the backup knows it is deliberate and not
   its responsibility.

### Declaring intent

Folder names cover most of it. What they cannot express — "stop me before you copy my data", "that
mount is not mine" — goes in container labels:

```yaml
services:
  db:
    labels:
      backup.policy: cold          # a live tar of a database is a corrupt database
      stack.role: database
  app:
    labels:
      backup.policy: hot
      backup.exclude: /volume1/photo   # mounted, but not this stack's data
      stack.docs: ./README.md
```

**Why labels and not a sidecar file:** on the audited host, *all 17 containers* carried their labels
in `docker inspect` while *10 had no compose file on disk at all*. Labels are the one channel that
survives a lost definition. A `stack.yaml` beside the compose file would read more nicely and would
have been useless for exactly the ten containers that needed it most.

### Profiles, not one imposed standard

One standard would be rejected by anyone with an existing host. Better to recognise several, score
whichever is found, and only offer to migrate:

- **A — Tidy in place.** Change nothing about where files live; add docs, pin digests, attach labels.
  Fixes findings 5–14.
- **B — Self-contained stacks.** The layout above. What a wizard should produce by default.
- **C — Volumes first.** Keep Docker in charge of state, but never anonymously: every volume named
  after its stack and service, and labelled.
- **D — Stacks in git.** B plus a repository, data and secrets ignored, secrets encrypted at rest.

### Capabilities, in build order

1. **Audit** — read-only, safe on day one, and it produces the work list everything else consumes.
2. **New stack** — a wizard that lays out the directory, pins the digest, writes the README skeleton,
   sets the permissions, attaches the labels, and registers the project.
3. **Adopt** — take an existing scattered container into the layout, with a dry run and a rollback.
   Where the tool earns its keep, and where it can do the most damage.
4. **Document** — generate READMEs and the index from live state.
5. **Secrets** — find plaintext credentials, move them, mark them for encryption, never print them.
6. **Pin & update** — resolve floating tags to digests, show what an update changes, keep the old
   digest for rollback.
7. **Back up & restore** — what exists today, reading declared intent instead of inferring it.
8. **Drift** — diff running containers against their compose files and flag hand-edits.

---

## 7. Known limitations

**Approximated containers.** Ten of the seventeen had no compose project, so they are rebuilt from
compose generated out of inspect data. That reconstruction does not carry `mem_limit`, `sysctls`,
`ulimits`, `logging`, `depends_on` or `security_opt`. The raw inspect JSON is archived alongside, so
nothing is lost — but it is not applied automatically.

**Shared folders must pre-exist.** On a fresh NAS, create them in DSM's Control Panel first, or
restored files land in a plain directory with the wrong permissions.

**Some apps refuse to be split.** Home Assistant writes settings and its database into one
directory; Postgres has a single data directory. The `config`/`data` distinction is a convention the
layout offers, not something it can enforce.

**ACLs depend on the host's tar.** DSM keeps ACLs in extended attributes, which plain tar drops. The
app probes `tar --help` once per connection and adds `--xattrs --xattrs-include='*' --acls` when
available — the include pattern matters, because GNU tar stores only `user.*` xattrs by default and
Synology's live in `system.*` and `security.*`. Where the host's tar lacks support you get a warning
and permissions still come back; only the ACLs do not.

**A stack whose compose directory is not on the host cannot be registered** with Container Manager by
anyone. Reported as an explained note rather than a failure.

**The backup folder is sensitive.** The inspect JSON and generated compose reproduce every container
environment variable in plaintext, which for most stacks means database passwords and API tokens.
Encrypt it before it goes off-site. Handling this properly — detecting secrets and encrypting them
rather than archiving them in the clear — is §6, capability 5, and is not built yet.

**A private label vocabulary is still private.** Four labels nobody else reads is a standard of one.
It is worth it only because the tool writes them itself. If it ever needs a person to maintain it by
hand, it has failed.

**The tool must not become the thing that needs backing up.** Every structure here has to be readable
by someone with an SSH session and no app. A layout only this tool understands has recreated the
original problem with extra steps.

---

## Sources

Claims about other projects were checked rather than recalled:

- [offen/docker-volume-backup](https://github.com/offen/docker-volume-backup) and its
  [stop-containers-during-backup guide](https://offen.github.io/docker-volume-backup/how-tos/stop-containers-during-backup.html)
- [Dockge](https://github.com/louislam/dockge)
- [What Portainer's backup does not include](https://oneuptime.com/blog/post/2026-08-02-portainer-backup-restore-limitations/view)
- [Escaping Portainer: moving stacks to Dockge or Komodo](https://www.bigiron.cc/guides/escaping-portainer-moving-your-stacks-to-dockge-or-komodo)
