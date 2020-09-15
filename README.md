# Azure Function Health

Azure Function Health is small library based on the aspnetcore HealthChecks feature. The traditional health checks registered in an aspnetcore API included the HealthCheckPublisherHostedService as a HostedService which is not possible or desired to run in an Azure Function. However there are benefits to included a health check in an Azure Function to test the depencies of your service. 

