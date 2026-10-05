package authz

default allow := false

# Public endpoints are always allowed
allow if { input.action in data.public_endpoints }

# Role-based: user's roles grant the requested permission
allow if { input.action in role_permissions }
