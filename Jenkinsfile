pipeline {
    agent any

    environment {
        IMAGE_NAME         = 'deepakrajv/meettomanagebackend'
        CONTAINER_NAME     = 'meettomanagebackend'
        GITHUB_REPO        = 'https://github.com/DeepakVijayasarathi/meettomanagebackend.git'

        // Non-secret settings only. Every password/key comes from Jenkins credentials
        // (see "Jenkins setup required" at the bottom) -- never commit secret values here.
        DB_HOST            = 'postgres'
        DB_PORT            = '5432'
        // Shared master registry database -- create it once on the server (empty); the API
        // migrates it on startup. Client databases are created by the API at provisioning
        // time, so DB_USER must have CREATEDB (the postgres superuser does).
        DB_NAME            = 'meettomanage_master'
        DB_USER            = 'postgres'

        STORAGE_S3_ENDPOINT      = 'hel1.your-objectstorage.com'
        STORAGE_S3_BUCKET_NAME   = 'readernest'

        // Replace with the real public URLs. CORS must be the exact frontend origin, never '*'.
        FRONTEND_BASE_URL    = 'https://app.example.com'
        API_BASE_URL         = 'https://api.example.com'
        PLATFORM_ADMIN_EMAIL = 'platform-admin@example.com'

        DOCKER_NETWORK     = 'reader-network'
    }

    triggers {
        githubPush()
    }

    stages {

        stage('Checkout') {
            steps {
                git(
                    branch: 'main',
                    url: "${GITHUB_REPO}",
                    credentialsId: 'github'
                )
            }
        }

        stage('Quality Gate') {
            steps {
                sh '''
                    dotnet restore iucs.meettomanage.api.sln
                    dotnet build iucs.meettomanage.api.sln --configuration Release --no-restore
                    dotnet test iucs.meettomanage.api.sln --configuration Release --no-build
                '''
            }
        }

        stage('Build Docker Image') {
            steps {
                script {
                env.RELEASE_VERSION = "${env.BUILD_NUMBER}-${env.GIT_COMMIT.take(12)}"
                sh """
                    docker build \
                      --build-arg RELEASE_VERSION=${RELEASE_VERSION} \
                      --build-arg GIT_COMMIT=${GIT_COMMIT} \
                      -t ${IMAGE_NAME}:${RELEASE_VERSION} \
                      -t ${IMAGE_NAME}:latest \
                      -f iucs.meettomanage.api/Dockerfile .
                """
                }
            }
        }

        stage('Create Docker Network') {
            steps {
                sh """
                    docker network inspect ${DOCKER_NETWORK} >/dev/null 2>&1 || \
                    docker network create ${DOCKER_NETWORK}
                """
            }
        }

        stage('Remove Old Container') {
            steps {
                sh """
                    docker stop ${CONTAINER_NAME} || true
                    docker rm ${CONTAINER_NAME} || true
                """
            }
        }

        stage('Run Container') {
            steps {
              script {
              // The marketing-site Hetzner AI assistant is optional: a missing credential must
              // not take the API down after the old container is already gone.
              def optionalCredentials = []
              try {
                  withCredentials([string(credentialsId: 'meettomanage-hetzner-ai-api-key', variable: 'HETZNER_AI_PROBE')]) {}
                  optionalCredentials << string(credentialsId: 'meettomanage-hetzner-ai-api-key', variable: 'HETZNER_AI_API_KEY')
              } catch (err) {
                  echo "WARNING: credential 'meettomanage-hetzner-ai-api-key' not found -- deploying without the chat assistant."
              }
              // Resend (HTTP email API) is optional too: without it, mail still goes through each
              // academy's own SMTP settings, and is only logged where none are set.
              try {
                  withCredentials([string(credentialsId: 'meettomanage-resend-api-key', variable: 'RESEND_PROBE')]) {}
                  optionalCredentials << string(credentialsId: 'meettomanage-resend-api-key', variable: 'RESEND_API_KEY')
              } catch (err) {
                  echo "WARNING: credential 'meettomanage-resend-api-key' not found -- deploying without Resend email."
              }
              withCredentials([
                  string(credentialsId: 'meettomanage-db-password',          variable: 'DB_PASS'),
                  string(credentialsId: 'meettomanage-jwt-signing-key',      variable: 'JWT_SIGNING_KEY'),
                  string(credentialsId: 'meettomanage-s3-access-key',        variable: 'S3_ACCESS_KEY'),
                  string(credentialsId: 'meettomanage-s3-secret-key',        variable: 'S3_SECRET_KEY'),
                  string(credentialsId: 'meettomanage-ssh-password-server0', variable: 'SSH_PASS_0'),
                  string(credentialsId: 'meettomanage-ssh-password-server1', variable: 'SSH_PASS_1'),
                  string(credentialsId: 'meettomanage-platform-admin-hash',  variable: 'PLATFORM_ADMIN_HASH'),
              ] + optionalCredentials) {
                // Secrets are escaped so the shell (not Groovy) expands them and Jenkins masks them.
                sh """
                    docker run -d \
                      --name ${CONTAINER_NAME} \
                      --network ${DOCKER_NETWORK} \
                      -e Jwt__SigningKey="\${JWT_SIGNING_KEY}" \
                      -p 2200:8080 \
                      --restart unless-stopped \
                      -e ASPNETCORE_ENVIRONMENT=Production \
                      -e ASPNETCORE_URLS=http://+:8080 \
                       -e Storage__S3__Endpoint="${STORAGE_S3_ENDPOINT}" \
                      -e Storage__S3__AccessKey="\${S3_ACCESS_KEY}" \
                      -e Storage__S3__SecretKey="\${S3_SECRET_KEY}" \
                      -e Storage__S3__BucketName="${STORAGE_S3_BUCKET_NAME}" \
                      -e Monitoring__Servers__0__SshPassword="\${SSH_PASS_0}" \
                      -e Monitoring__Servers__1__SshPassword="\${SSH_PASS_1}" \
                      -e HetznerAi__ApiKey="\${HETZNER_AI_API_KEY:-}" \
                      -e Resend__ApiKey="\${RESEND_API_KEY:-}" \
                      -e Resend__From="Meet to Manage <notifications@meettomanage.cloud>" \
                      -e Marketing__LeadAlertEmail="deepanvijayasarathi32@gmail.com" \
                      -e Seed__MarketingEmail="marketing@meettomanage.cloud" \
                      -e Seed__MarketingPin="1234" \
                      -e MultiTenancy__Enabled=true \
                      -e ConnectionStrings__MasterDb="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=\${DB_PASS}" \
                      -e ConnectionStrings__TenantServer="Host=${DB_HOST};Port=${DB_PORT};Database=postgres;Username=${DB_USER};Password=\${DB_PASS};Maximum Pool Size=20" \
                      -e MultiTenancy__PlatformAdmin__Email="${PLATFORM_ADMIN_EMAIL}" \
                      -e MultiTenancy__PlatformAdmin__PasswordHash="\${PLATFORM_ADMIN_HASH}" \
                      -e Database__MigrateOnStartup=true \
                      -e Frontend__BaseUrl="${FRONTEND_BASE_URL}" \
                      -e Api__BaseUrl="${API_BASE_URL}" \
                      -e Cors__AllowedOrigins__0="${FRONTEND_BASE_URL}" \
                      ${IMAGE_NAME}:${RELEASE_VERSION}
                """
              }
              }
            }
        }

        stage('Verify Deployment') {
            steps {
                sh """
                    sleep 15
                    docker ps | grep ${CONTAINER_NAME}
                    docker logs --tail 50 ${CONTAINER_NAME}
                    # A missing master/tenant connection fails startup, so require /health to answer.
                    HEALTH_JSON=\$(curl --fail --silent --show-error http://127.0.0.1:2200/health)
                    echo "\${HEALTH_JSON}"
                    echo "\${HEALTH_JSON}" | grep -F '"version":"${RELEASE_VERSION}"'
                    echo "\${HEALTH_JSON}" | grep -F '"commit":"${GIT_COMMIT}"'
                """
            }
        }
    }

    post {
        success {
            echo '✅ ReaderNest Backend deployed successfully on port 2200'
        }

        failure {
            echo '❌ Deployment failed. Check Jenkins console logs.'
        }

        always {
            cleanWs()
        }
    }
}

// ---------------------------------------------------------------------------------------
// Jenkins setup required (one-time): Manage Jenkins -> Credentials -> Global -> Add
// Kind = "Secret text" for each ID below. Real values are NOT stored in this repo.
//
//   meettomanage-db-password           (Postgres password for DB_USER)
//   meettomanage-jwt-signing-key       (>= 32 bytes; rotating it invalidates all sessions)
//   meettomanage-s3-access-key
//   meettomanage-s3-secret-key
//   meettomanage-ssh-password-server0  (Monitoring server 0)
//   meettomanage-ssh-password-server1  (Monitoring server 1)
//   meettomanage-platform-admin-hash   (BCrypt hash from
//                                       `dotnet iucs.meettomanage.api.dll platform-password-hash`)
//   meettomanage-hetzner-ai-api-key    (optional)
//   meettomanage-resend-api-key        (optional; Resend email API key, used when an
//                                       academy has no SMTP set up, e.g. new-lead alerts)
// ---------------------------------------------------------------------------------------
