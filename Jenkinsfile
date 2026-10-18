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

        stage('Build Docker Image') {
            steps {
                sh """
                    docker build \
                      -t ${IMAGE_NAME}:latest \
                      -f iucs.meettomanage.api/Dockerfile .
                """
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
              // The marketing-site chat assistant (Gemini) is optional: a missing credential must
              // not take the API down after the old container is already gone.
              def optionalCredentials = []
              try {
                  withCredentials([string(credentialsId: 'meettomanage-gemini-api-key', variable: 'GEMINI_PROBE')]) {}
                  optionalCredentials << string(credentialsId: 'meettomanage-gemini-api-key', variable: 'GEMINI_API_KEY')
              } catch (err) {
                  echo "WARNING: credential 'meettomanage-gemini-api-key' not found -- deploying without the chat assistant."
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
                      -e Gemini__ApiKey="\${GEMINI_API_KEY:-}" \
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
                      ${IMAGE_NAME}:latest
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
                    curl --fail --silent --show-error http://127.0.0.1:2200/health
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
//   meettomanage-gemini-api-key        (optional)
// ---------------------------------------------------------------------------------------
