---
sidebar_position: 8
title: Who may open /admin
---

# Who may open `/admin`

**Admin → Sign-in access.** Everyone signs in through the platform's LoginLink organization first; this decides who gets in.

| Mode | |
|---|---|
| **Only people I approve** (default, recommended) | New people wait in the list until an admin approves them as **admin** or **support**, or declines them. |
| **Auto-approve the organization's LoginLink admins** | Anyone whose LoginLink token says `user_kind = tenant_admin` gets admin access. |
| **Anyone with a PayLink platform role in LoginLink** | `paylink_platform_admin` gives admin access, `paylink_platform_support` read-only access. Removing the role in LoginLink removes access at the next sign-in. |

| Access | Can |
|---|---|
| **Admin** | Everything in `/admin`. |
| **Support** | Read everything in `/admin`, change nothing. |

The list shows each person, their access, how they got it (first admin at setup, let in automatically, approved by whom) and their last sign-in. Admins can approve, make someone read-only, or remove them. Every change is in the [platform audit log](../security/audit-logs.md).
