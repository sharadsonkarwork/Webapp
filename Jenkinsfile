// Rosterix 2.0 pipeline (mirrors .github/workflows/ci.yml). No credentials are embedded: registry and cluster
// access come from Jenkins credentials bound at run time.
pipeline {
  agent any
  options { timestamps(); disableConcurrentBuilds() }
  environment { DOTNET_CLI_TELEMETRY_OPTOUT = '1'; CI = 'true' }
  stages {
    stage('Backend') {
      agent { docker { image 'mcr.microsoft.com/dotnet/sdk:8.0' } }
      steps { sh 'dotnet restore && dotnet build -c Release --no-restore && dotnet test -c Release --no-build --logger trx' }
      post { always { junit allowEmptyResults: true, testResults: '**/TestResults/*.trx' } }
    }
    stage('Frontend') {
      agent { docker { image 'node:22' } }
      steps { dir('src/Rosterix.Web') { sh 'npm ci && npx jest && npx ng build --configuration production' } }
    }
    stage('E2E and accessibility') {
      agent { dockerfile { filename 'deploy/Dockerfile.ci'; args '--ipc=host' } }
      steps { dir('src/Rosterix.Web') { sh 'npm ci && npx playwright test' } }
    }
    stage('Images') {
      when { branch 'main' }
      steps {
        withCredentials([usernamePassword(credentialsId: 'registry', usernameVariable: 'REG_USER', passwordVariable: 'REG_PASS')]) {
          sh '''docker login -u "$REG_USER" --password-stdin "$REGISTRY" <<< "$REG_PASS"
                docker build -f deploy/Dockerfile.api -t "$REGISTRY/rosterix-api:$GIT_COMMIT" . && docker push "$REGISTRY/rosterix-api:$GIT_COMMIT"
                docker build -f deploy/Dockerfile.web -t "$REGISTRY/rosterix-web:$GIT_COMMIT" . && docker push "$REGISTRY/rosterix-web:$GIT_COMMIT"'''
        }
      }
    }
    stage('Deploy to staging') {
      when { branch 'main' }
      steps { sh 'helm upgrade --install rosterix deploy/helm/rosterix --namespace rosterix --set image.api.tag=$GIT_COMMIT --set image.web.tag=$GIT_COMMIT' }
    }
  }
}
