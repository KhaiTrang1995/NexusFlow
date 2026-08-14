// Phase 0 of docs/28-Azure-Hosting.md §7: one region, all three roles in one app, no HA,
// no broker, and managed identity to the database from the first deployment.
//
// Deliberately NOT here, and each for a reason written down rather than forgotten:
//   * PgBouncer. FlowX cannot reach its schema through a transaction pooler today — the
//     adapter selects the schema with a startup parameter the pooler rejects or discards.
//     CHECKLIST blocker B-6. Turning it on would produce a deployment that starts and then
//     answers `relation "flow_instance" does not exist`.
//   * Zone-redundant HA and a read replica. Phase 2 and Phase 4.
//   * Service Bus. Phase 3, and only if fan-out is genuinely needed: events already flow
//     through the outbox and the change feed.
//   * Private endpoints. They need a VNet the subscription may not have; §9 of the README
//     beside this file says what to add and when.

targetScope = 'resourceGroup'

@description('Short name distinguishing this deployment. Lower-case letters and digits.')
@minLength(3)
@maxLength(12)
param name string

@description('Where everything goes. Defaults to the resource group\'s region.')
param location string = resourceGroup().location

@description('Container image for the FlowX host, including its tag.')
param image string

@description('Object id of the Entra principal that administers PostgreSQL.')
param databaseAdministratorObjectId string

@description('Display name of that principal, as it appears in Entra.')
param databaseAdministratorName string

@description('Schema the journal owns. Must match FlowX\'s PostgresJournalOptions.Schema.')
param schema string = 'flowx'

@description('vCores for PostgreSQL. Four is the size the durability rig has numbers for.')
@allowed([ 'Standard_D2ds_v5', 'Standard_D4ds_v5', 'Standard_D8ds_v5' ])
param databaseSku string = 'Standard_D4ds_v5'

@description('Storage for PostgreSQL, in GiB. IOPS scale with size, and the journal is write-heavy.')
@minValue(32)
param databaseStorageGb int = 128

var suffix = uniqueString(resourceGroup().id)
var databaseName = 'flowx'

// ── Identity ────────────────────────────────────────────────────────────────
// One user-assigned identity, used to pull the image and to authenticate to PostgreSQL.
// A system-assigned identity would be simpler and cannot be granted database access before
// the app exists, which is a chicken-and-egg this avoids.
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-${suffix}'
  location: location
}

// ── Telemetry ───────────────────────────────────────────────────────────────
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}-${suffix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    // A daily cap, because ingestion is the line that surprises people (28 §11).
    workspaceCapping: { dailyQuotaGb: 5 }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}-${suffix}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ── Data ────────────────────────────────────────────────────────────────────
resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-${name}-${suffix}'
  location: location
  sku: {
    name: databaseSku
    // General Purpose rather than Burstable: Burstable cannot take the zone-redundant HA
    // Phase 2 turns on, and moving tier later is a restart.
    tier: 'GeneralPurpose'
  }
  properties: {
    version: '16'
    storage: {
      storageSizeGB: databaseStorageGb
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      // Phase 2. Named here rather than omitted so the diff that enables it is one word.
      mode: 'Disabled'
    }
    authConfig: {
      // No password exists to leak or rotate. This is the single highest-value control in
      // the whole file, and it is why the identity above is created first.
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: subscription().tenantId
    }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// The human or group that runs migrations and can grant the application its role.
resource databaseAdministrator 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: postgres
  name: databaseAdministratorObjectId
  properties: {
    principalType: 'User'
    principalName: databaseAdministratorName
    tenantId: subscription().tenantId
  }
}

// The application's identity is deliberately NOT registered as a server administrator.
// Two reasons, and the first is the one that matters: an administrator can read and drop
// every database on the server, and the host needs exactly one schema in one of them. The
// second is mechanical — a user-assigned identity's object id is known only once it exists,
// so it is not a deploy-time value and this resource could not be written anyway.
//
// The grant is one statement, run once by the administrator above, and the README beside
// this file carries it:
//
//   SELECT * FROM pgaadauth_create_principal('<identity name>', false, false);
//
// Phase 0 reaches the database over the public endpoint from the Container Apps environment.
// Narrow this the moment a VNet exists; it is the widest thing in the file.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'allow-azure-services'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
  dependsOn: [ database ]
}

// ── Compute ─────────────────────────────────────────────────────────────────
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}-${suffix}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${name}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
    }
    template: {
      containers: [
        {
          name: 'flowx'
          image: image
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'FLOWX_POSTGRES_CONNECTION'
              // No password: Npgsql obtains an Entra token through the managed identity.
              // Maximum Pool Size is capped deliberately — replicas multiply it, and
              // PostgreSQL counts connections (28 §4.1). Do not raise it without raising
              // max_connections and re-reading that section.
              value: join([
                'Host=${postgres.properties.fullyQualifiedDomainName}'
                'Database=${databaseName}'
                'Username=${identity.name}'
                'SearchPath=${schema}'
                'SslMode=Require'
                'Maximum Pool Size=15'
              ], ';')
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: identity.properties.clientId
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: insights.properties.ConnectionString
            }
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/health/ready', port: 8080 }
              failureThreshold: 30
              periodSeconds: 5
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              periodSeconds: 10
            }
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        // Phase 0 runs all three roles in one app, and the scheduler and worker sweeps mean
        // it cannot scale to zero: a sleeping sweeper misses firings and leaves events in
        // the outbox (ADR-0076). Phase 1 splits the roles and only this one gets a zero.
        minReplicas: 1
        maxReplicas: 3
        rules: [
          {
            name: 'concurrent-requests'
            http: { metadata: { concurrentRequests: '50' } }
          }
        ]
      }
    }
  }
}

// ── Web client ──────────────────────────────────────────────────────────────
resource web 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'stapp-${name}-${suffix}'
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    // Built and uploaded by the workflow rather than from a linked repository, so the same
    // artifact the tests ran against is the one deployed.
    buildProperties: { skipGithubActionWorkflowGeneration: true }
  }
}

@description('The API\'s public address.')
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'

@description('The web client\'s public address.')
output webUrl string = 'https://${web.properties.defaultHostname}'

@description('The PostgreSQL host, for the migration step.')
output databaseHost string = postgres.properties.fullyQualifiedDomainName

@description('The application identity\'s name, which is also its database role.')
output applicationIdentityName string = identity.name
